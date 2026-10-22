using System.Collections.Immutable;
using Corvid.Abstractions.Fabric;
using Starlings.Models.Kev;
using Starlings.Runtime.Onnx;

namespace Corvid.Models.Starling;

/// <summary>Kev's two stages (encode, then run the state once and each question's row), so the adapter is tested without a model.</summary>
internal interface IKevRunner
{
    /// <exception cref="KevInputTooLongException">The state or a question's row (state plus branch) is over Kev's limits.</exception>
    KevEncoding Encode(KevRecord record);

    /// <summary>Calibrated probabilities per row, in Kev's option order.</summary>
    IReadOnlyList<float[]> Run(KevEncoding encoding, CancellationToken cancellationToken);
}

/// <summary>
/// The <c>decider</c> slot over Starling's <see cref="KevDecisionModel"/> (plan §5): one <see cref="KevRequest"/> per
/// checkpoint, the TurnState as <see cref="KevContent.Json"/> and every question as its own row on it, with its
/// instructions (text or JSON) and its options rendered <c>name: description</c>. Answers keep Kev's calibrated
/// probabilities in the question's option order, so <see cref="DecisionAnswer.Confidence"/> and
/// <see cref="DecisionAnswer.ExpectedScore"/> follow, and are <see cref="DecisionAnswer.Calibrated"/>: the model always runs
/// with its head's calibrated temperature.
/// </summary>
/// <remarks>
/// <para>
/// Limits (plan §2, §5.1). A state over Kev's 8,192 tokens returns <see cref="DeciderUnavailableReason.StateOverBudget"/>,
/// so the fabric falls back to the degraded tier; nothing is truncated. A question whose own row (its instructions and
/// options, after the shared state) is over the row budget (1,000 tokens by default, the size Kev's latency was measured
/// at) or over Kev's own 8,192-token row limit is declined: it gets no answer, so it takes its below-floor action, while
/// the rest of the checkpoint stays calibrated. When every question is declined the request is
/// <see cref="DeciderUnavailableReason.Refused"/>.
/// </para>
/// <para>
/// <see cref="DecideAsync"/> runs Kev synchronously on the calling thread (about 0.4 s per question on CPU, 0.2-0.3 s per
/// ingress checkpoint on an RTX 3090 in spike S1) and returns a completed task. The host schedules it on the device scheduler at fabric priority (C-011), never on a session actor.
/// </para>
/// </remarks>
public sealed class KevDecider : IDecider, IDisposable
{
    /// <summary>Plan §5.1: rows up to 1,000 tokens per question; Kev's 0.37 s per question was measured with rows up to 853.</summary>
    public const int DefaultRowBudget = 1_000;

    /// <summary>The reason recorded for a question whose row is over the budget.</summary>
    public const string RowOverBudget = "row_over_budget";

    /// <summary>The reason recorded for a question Kev cannot take, for example more than 255 options.</summary>
    public const string Unsupported = "unsupported_question";

    private readonly IKevRunner _runner;
    private readonly IDisposable? _owned;
    private readonly int _rowBudget;

    internal KevDecider(string name, IKevRunner runner, IDisposable? owned = null, int rowBudget = DefaultRowBudget,
        string placement = ModelPlacement.Cpu)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentOutOfRangeException.ThrowIfLessThan(rowBudget, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(placement);
        Name = name;
        _runner = runner;
        _owned = owned;
        _rowBudget = rowBudget;
        Placement = placement;
    }

    /// <summary>
    /// Loads Kev with the head's calibrated temperature, on the CPU by default or on a GPU when
    /// <paramref name="options"/> asks for <see cref="OnnxExecutionProvider.Cuda"/> (C-029).
    /// </summary>
    /// <remarks>
    /// On Linux, a long-running process should start with <c>MALLOC_MMAP_THRESHOLD_=65536</c> (Starling's Kev README).
    /// CUDA needs the CUDA natives (a <c>-p:UseCuda=true</c> build), checked before the roughly 3 GB model is read, and
    /// the CUDA 13 runtime (cudart, cuBLAS, cuRAND, cuFFT) on the loader path, which ONNX Runtime checks when the session
    /// starts. cuDNN 9 is optional: the CUDA provider dlopens it only for kernels that use it, and Kev's graph ran without it.
    /// Either failure is a <see cref="ModelPlacementUnavailableException"/>; nothing falls back to the CPU here.
    /// </remarks>
    /// <param name="rowBudget">Tokens one question's row may take after the state; larger rows are declined.</param>
    /// <exception cref="ModelPlacementUnavailableException">CUDA was asked for and cannot be used.</exception>
    public static KevDecider Create(KevModelFiles files, KevOptions? options = null, int rowBudget = DefaultRowBudget)
    {
        ArgumentNullException.ThrowIfNull(files);
        // Answers are recorded as calibrated, and calibration tooling (C-021) fits floors only on those; raw probabilities
        // (temperature 1.0) or any other temperature would make that flag false.
        if (options?.Temperature is not null)
            throw new ArgumentException("Corvid records Kev's answers as calibrated, so the head's calibrated temperature must apply.", nameof(options));
        var name = NameOf(files.Profile.Model);
        var placement = PlacementOf(options?.Session);
        if (placement != ModelPlacement.Cpu)
            RequireCudaProvider(name, placement);
        KevDecisionModel model;
        try
        {
            model = KevDecisionModel.Create(files, options);
        }
        catch (Exception exception) when (placement != ModelPlacement.Cpu
                                          && exception is OnnxProviderUnavailableException or OnnxRuntimeUnavailableException)
        {
            throw new ModelPlacementUnavailableException(name, placement, $"{name} cannot run on {placement}: {exception.Message}", exception);
        }
        return new KevDecider(name, new ModelRunner(model), model, rowBudget, placement);
    }

    /// <summary>The placement a session's options ask for: <c>cpu</c>, or <c>cuda:N</c> for the CUDA provider on device N.</summary>
    public static string PlacementOf(OnnxSessionOptions? session) => session switch
    {
        null or { Provider: OnnxExecutionProvider.Cpu } => ModelPlacement.Cpu,
        { Provider: OnnxExecutionProvider.Cuda } => ModelPlacement.Cuda(session.CudaDeviceId),
        _ => throw new NotSupportedException($"ONNX execution provider {session.Provider} is not supported; use Cpu or Cuda."),
    };

    /// <summary>The options that place Kev on <paramref name="placement"/> (<c>cpu</c> or <c>cuda:N</c>); null for the CPU.</summary>
    public static KevOptions? OptionsFor(string placement)
    {
        if (!ModelPlacement.TryParse(placement, out _, out var device))
            throw new ArgumentException($"'{placement}' is not a placement: cpu or cuda:N.", nameof(placement));
        return device is { } ordinal
            ? new KevOptions { Session = new OnnxSessionOptions { Provider = OnnxExecutionProvider.Cuda, CudaDeviceId = ordinal } }
            : null;
    }

    /// <summary>
    /// Whether this process's ONNX Runtime has the CUDA provider compiled in: true in a <c>-p:UseCuda=true</c> build,
    /// false in the default CPU build, null (with the reason) when the native runtime does not load at all. A listed
    /// provider can still fail to start without the CUDA runtime (cudart, cuBLAS, cuRAND, cuFFT) or a visible device.
    /// </summary>
    public static bool? HasCudaProvider(out string? problem)
    {
        problem = null;
        try
        {
            return OnnxRuntimeInfo.AvailableProviders.Contains(OnnxRuntimeInfo.CudaProviderName, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is OnnxRuntimeUnavailableException or DllNotFoundException or TypeInitializationException)
        {
            problem = exception.Message;
            return null;
        }
    }

    private static void RequireCudaProvider(string name, string placement)
    {
        switch (HasCudaProvider(out var problem))
        {
            case null:
                throw new ModelPlacementUnavailableException(name, placement,
                    $"{name} cannot run on {placement}: ONNX Runtime did not load ({problem}).");
            case false:
                throw new ModelPlacementUnavailableException(name, placement,
                    $"{name} cannot run on {placement}: this build has the CPU natives (no {OnnxRuntimeInfo.CudaProviderName}); " +
                    "build with -p:UseCuda=true.");
        }
    }

    /// <summary>The decider name recorded on decisions, which is also the catalogue name: <c>kev-0.8b</c> or <c>kev-4b</c>.</summary>
    public static string NameOf(KevModel model) => model switch
    {
        KevModel.Kev08B => "kev-0.8b",
        KevModel.Kev4B => "kev-4b",
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unknown Kev model."),
    };

    public string Name { get; }

    /// <summary>
    /// Where this decider runs: <c>cpu</c> or <c>cuda:N</c> (C-029). A small, stable flag for C-030, which reduces
    /// checkpoints when the decider is not on a GPU (<see cref="ModelPlacement.IsCuda"/>).
    /// </summary>
    public string Placement { get; }

    public ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Decide(request, cancellationToken));
    }

    private DeciderResult Decide(DecisionRequest request, CancellationToken cancellationToken)
    {
        var state = KevContent.Json(request.State);
        var declined = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        // Ordinal key order keeps the request, and so Kev's rows, deterministic.
        var pending = new List<(string Key, DecisionQuestion Question, KevQuestion Kev)>();
        foreach (var (key, question) in request.Questions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (ToKev(question) is { } kev)
                pending.Add((key, question, kev));
            else
                declined[key] = Unsupported;
        }

        KevEncoding encoding;
        while (true)
        {
            if (pending.Count == 0)
                return Refused(declined);
            try
            {
                encoding = _runner.Encode(new KevRequest(state, pending.Select(item => KeyValuePair.Create(item.Key, item.Kev))).ToRecord());
                break;
            }
            catch (KevInputTooLongException exception) when (exception.QuestionIndex is null)
            {
                // §5.1: a state Kev refuses means the fabric is unavailable for this checkpoint.
                return new DeciderUnavailable(Name, DeciderUnavailableReason.StateOverBudget,
                    $"state is {exception.TokenCount} tokens; Kev accepts {exception.MaxTokens}");
            }
            catch (KevInputTooLongException exception) when (exception.QuestionIndex is { } index && index >= 0 && index < pending.Count)
            {
                // A row over Kev's own limit: decline that question and encode the rest again.
                declined[pending[index].Key] = RowOverBudget;
                pending.RemoveAt(index);
            }
        }

        if (encoding.Rows.Count != pending.Count)
            return new DeciderUnavailable(Name, DeciderUnavailableReason.Failed,
                $"Kev encoded {encoding.Rows.Count} rows for {pending.Count} questions");
        var kept = new List<int>();
        for (var i = 0; i < pending.Count; i++)
        {
            if (encoding.Rows[i].Ids.Length <= _rowBudget)
                kept.Add(i);
            else
                declined[pending[i].Key] = RowOverBudget;
        }
        if (kept.Count == 0)
            return Refused(declined);
        // Rows are independent continuations of the state, so the declined ones are dropped without encoding again.
        if (kept.Count < pending.Count)
            encoding = new KevEncoding(encoding.StateIds, [.. kept.Select(i => encoding.Rows[i])]);

        var probabilities = _runner.Run(encoding, cancellationToken);
        if (probabilities.Count != kept.Count)
            return new DeciderUnavailable(Name, DeciderUnavailableReason.Failed,
                $"Kev returned {probabilities.Count} answers for {kept.Count} questions");

        var answers = ImmutableDictionary.CreateBuilder<string, DecisionAnswer>(StringComparer.Ordinal);
        for (var row = 0; row < kept.Count; row++)
        {
            var (key, question, _) = pending[kept[row]];
            if (ToAnswer(question, probabilities[row]) is { } answer)
                answers[key] = answer;
            else
                return new DeciderUnavailable(Name, DeciderUnavailableReason.Failed,
                    $"Kev's answer to '{key}' does not fit the question");
        }
        return new DeciderAnswers(Name, answers.ToImmutable()) { Declined = declined.ToImmutable() };
    }

    private DeciderUnavailable Refused(ImmutableDictionary<string, string>.Builder declined) =>
        new(Name, DeciderUnavailableReason.Refused,
            "every question was declined: " + string.Join(", ", declined.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key} ({pair.Value})")));

    /// <summary>The question as Kev asks it, or null when Kev cannot (more than <see cref="KevModelProfile.MaxOptions"/> options).</summary>
    internal static KevQuestion? ToKev(DecisionQuestion question)
    {
        if (question.Options.Length > KevModelProfile.MaxOptions)
            return null;
        var instructions = question.Instructions.Json is { } json ? KevContent.Json(json) : KevContent.Text(question.Instructions.Text!);
        return question.Kind switch
        {
            // Kev's options are no, yes (System One criteria false and true); Corvid's are yes, no. ToAnswer swaps them back.
            QuestionKind.Noul => KevQuestion.Noul(instructions),
            QuestionKind.Choice => KevQuestion.Choice(instructions, question.Options.Select(option =>
                KeyValuePair.Create(option.Name, option.Description is null ? null : KevContent.Text(option.Description)))),
            // Score levels are positional (keys "0", "1", …), read as name: description like any option.
            QuestionKind.Score => KevQuestion.Score(instructions, question.Options.Select(option => KevContent.Text(option.ToString()))),
            _ => throw new ArgumentOutOfRangeException(nameof(question)),
        };
    }

    /// <summary>Kev's probabilities in Corvid's option order, normalised in double precision; null when the shape is wrong.</summary>
    internal static DecisionAnswer? ToAnswer(DecisionQuestion question, IReadOnlyList<float> probabilities)
    {
        if (probabilities.Count != question.Options.Length || probabilities.Any(p => !float.IsFinite(p) || p < 0))
            return null;
        IEnumerable<double> ordered = question.Kind == QuestionKind.Noul
            ? [probabilities[1], probabilities[0]]
            : probabilities.Select(p => (double)p);
        var values = ordered.ToArray();
        var sum = values.Sum();
        if (sum <= 0)
            return null;
        return DecisionAnswer.For(question, values.Select(p => p / sum), calibrated: true);
    }

    public void Dispose() => _owned?.Dispose();

    private sealed class ModelRunner(KevDecisionModel model) : IKevRunner
    {
        public KevEncoding Encode(KevRecord record) => model.Encode(record);

        public IReadOnlyList<float[]> Run(KevEncoding encoding, CancellationToken cancellationToken) => model.Run(encoding, cancellationToken);
    }
}

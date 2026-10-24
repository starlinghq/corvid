using System.Globalization;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Secrets;

/// <summary>
/// A secret as the Secrets table draws it: name, what it is for, bound hosts, method policy, toolsets and role, budget,
/// who uses it and when it was last used. Never a value: <see cref="SecretInfo"/> has none to show.
/// </summary>
public sealed class SecretRowView
{
    internal SecretRowView(SecretInfo secret, IReadOnlyList<SecretConsumer> users, TimeProvider time)
    {
        Secret = secret;
        Name = SafeText.Clean(secret.Name);
        HasHidden = SafeText.HasHidden(secret.Name);
        Description = SafeText.Clean(secret.Description);
        Hosts = string.Join(", ", secret.Hosts.Select(SafeText.Clean));
        Methods = [.. secret.Methods.Select(rule => new StatusTag(
            $"{SafeText.Clean(rule.Method)} {SecretsText.Policy(rule.Policy)}", SecretsText.PolicyTone(rule.Policy), SecretsText.PolicyHelp(rule.Policy)))];
        var toolsets = string.Join(", ", secret.Toolsets.Select(SafeText.Clean));
        Use = secret.Role is { Length: > 0 } role ? $"{toolsets} · role {SafeText.Clean(role)}" : toolsets;
        Budget = secret.BudgetLimit is { } limit
            ? string.Create(CultureInfo.InvariantCulture, $"{secret.BudgetUsed} of {limit} this {secret.BudgetWindow}")
            : "no ceiling";
        BudgetFraction = secret.BudgetLimit is { } max && max > 0 ? Math.Clamp((double)secret.BudgetUsed / max, 0, 1) : 0;
        UsedBy = users.Count == 0 ? "nothing uses it" : string.Join(", ", users.Select(user => SafeText.Clean(user.Name)));
        LastUsed = secret.LastUsed is { } at ? "last used " + RuleTime(at, time) : "never used";
        TaintedUse = $"in a tainted turn: {SecretsText.Policy(secret.Tainted)}";
    }

    public SecretInfo Secret { get; }

    /// <summary>The name, cleaned for display.</summary>
    public string Name { get; }

    /// <summary>The name holds characters a reader cannot see; the row is marked and the name cannot be bound.</summary>
    public bool HasHidden { get; }

    public string Description { get; }

    public string Hosts { get; }

    /// <summary>"GET auto", "POST ask", "DELETE confirm".</summary>
    public IReadOnlyList<StatusTag> Methods { get; }

    /// <summary>"git, github", "model-provider · role model-provider".</summary>
    public string Use { get; }

    public string Budget { get; }

    public double BudgetFraction { get; }

    public bool HasBudget => Secret.BudgetLimit is not null;

    public string UsedBy { get; }

    public string LastUsed { get; }

    public string TaintedUse { get; }

    public string AutomationName =>
        $"{Name}{(HasHidden ? ", holds hidden characters" : "")}, {Description}, hosts {Hosts}, {string.Join(", ", Methods.Select(m => m.Text))}, {Use}, {Budget}, used by {UsedBy}, {LastUsed}";

    private static string RuleTime(DateTimeOffset at, TimeProvider time)
    {
        var zone = time.LocalTimeZone;
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return local.Date == TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("MMM d", CultureInfo.InvariantCulture);
    }
}

/// <summary>Why a configured feature cannot use its secret yet.</summary>
public enum NeedState
{
    Ready,
    /// <summary>Vault has no secret by that name.</summary>
    Missing,
    /// <summary>The secret exists but cannot serve the feature: not bound to its host, or a role that forbids it.</summary>
    Unusable,
}

/// <summary>
/// A feature waiting on a secret (F17): what it is, the name it waits on and the host it calls, why it waits, the exact
/// <c>corvid-vault set</c> command, and the two ways out: set it (outside the app), or bind the feature to a name Vault
/// already has.
/// </summary>
public sealed class SecretNeedView
{
    internal SecretNeedView(SecretConsumer consumer, NeedState state, string? refusal, Func<SecretNeedView, CancellationToken, Task> set, Action<SecretNeedView> bind)
    {
        Consumer = consumer;
        State = state;
        Feature = $"{SecretsText.Kind(consumer.Kind)} {SafeText.Clean(consumer.Name)}";
        Detail = SafeText.Clean(consumer.Detail);
        SecretName = SafeText.Clean(consumer.Secret);
        CanSet = SecretNames.IsValid(consumer.Secret);
        Command = SecretNames.SetCommand(consumer.Secret);
        Problem = Spans.Parse(state switch
        {
            NeedState.Missing => $"waits on `{Spans.Escape(SecretName)}`, which Vault does not have · it calls {Spans.Escape(SafeText.Clean(consumer.Host))}",
            _ => Spans.Escape(SafeText.Clean(refusal ?? "")),
        });
        SetCommand = new AsyncRelayCommand(ct => set(this, ct), () => CanSet);
        BindCommand = new RelayCommand(() => bind(this));
    }

    public SecretConsumer Consumer { get; }

    public NeedState State { get; }

    /// <summary>"model gemini", "gateway slack".</summary>
    public string Feature { get; }

    public string Detail { get; }

    public string SecretName { get; }

    /// <summary>The exact command, the name revealed: what is shown is what runs.</summary>
    public string Command { get; }

    /// <summary>The name is a valid secret name (no hidden characters, no spaces), so a set can start.</summary>
    public bool CanSet { get; }

    public IReadOnlyList<TextRun> Problem { get; }

    public AsyncRelayCommand SetCommand { get; }

    public RelayCommand BindCommand { get; }

    public string AutomationName => $"{Feature}, {Detail}, {string.Concat(Problem.Select(run => run.Text))}";
}

/// <summary>A configured feature and the secret it uses, for the "who uses what" list.</summary>
public sealed class ConsumerRowView(SecretConsumer consumer, NeedState state)
{
    public string Feature { get; } = $"{SecretsText.Kind(consumer.Kind)} {SafeText.Clean(consumer.Name)}";

    public string Secret { get; } = SafeText.Clean(consumer.Secret);

    public string Host { get; } = SafeText.Clean(consumer.Host);

    public string State { get; } = state switch
    {
        NeedState.Ready => "ready",
        NeedState.Missing => "waiting",
        _ => "refused",
    };

    public Tone StateTone { get; } = state == NeedState.Ready ? Tone.Green : Tone.Amber;

    public string AutomationName => $"{Feature} uses {Secret} for {Host}, {State}";
}

/// <summary>
/// A secret offered in the bind picker: its name revealed (the person binds exactly this name), its hosts, and whether it
/// can serve the feature, with why not.
/// </summary>
public sealed class BindChoiceView
{
    internal BindChoiceView(SecretInfo secret, SecretConsumer consumer, Func<BindChoiceView, CancellationToken, Task> choose)
    {
        Secret = secret;
        Name = SafeText.Reveal(secret.Name);
        HasHidden = SafeText.HasHidden(secret.Name);
        Hosts = string.Join(", ", secret.Hosts.Select(host => SafeText.Clean(host)));
        Reason = HasHidden
            ? "the name holds hidden characters, shown in brackets: correct it in Vault first"
            : SecretBinding.Refusal(secret, consumer) is { } refusal ? SafeText.Clean(refusal) : "";
        IsCurrent = secret.Name == consumer.Secret;
        CanChoose = Reason.Length == 0 && !IsCurrent;
        Choose = new AsyncRelayCommand(ct => choose(this, ct), () => CanChoose);
    }

    public SecretInfo Secret { get; }

    public string Name { get; }

    public bool HasHidden { get; }

    public string Hosts { get; }

    public string Reason { get; }

    public bool HasReason => Reason.Length > 0;

    public bool IsCurrent { get; }

    public bool CanChoose { get; }

    public AsyncRelayCommand Choose { get; }

    public string AutomationName => CanChoose ? $"Bind to {Name}, {Hosts}" : $"{Name}, {Hosts}, cannot be bound: {(IsCurrent ? "already bound" : Reason)}";
}

/// <summary>Words the Secrets page shares between its rows.</summary>
public static class SecretsText
{
    public static string Policy(MethodPolicy policy) => policy.ToString().ToLowerInvariant();

    public static Tone PolicyTone(MethodPolicy policy) => policy switch
    {
        MethodPolicy.Auto => Tone.Grey,
        MethodPolicy.Ask => Tone.Blue,
        MethodPolicy.Confirm => Tone.Violet,
        _ => Tone.Red,
    };

    public static string PolicyHelp(MethodPolicy policy) => policy switch
    {
        MethodPolicy.Auto => "Used without asking",
        MethodPolicy.Ask => "Corvid's approval prompt asks first",
        MethodPolicy.Confirm => "Vault asks you itself, out of band; Corvid cannot answer it",
        _ => "Vault refuses this method",
    };

    public static string Kind(ConsumerKind kind) => kind switch
    {
        ConsumerKind.Model => "model",
        ConsumerKind.Gateway => "gateway",
        ConsumerKind.Mcp => "MCP",
        _ => "tool",
    };
}

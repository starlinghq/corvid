using System.Buffers;
using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Fabric;

namespace Corvid.Memory;

/// <summary>A lesson to file: its content, the project it belongs to, and how it was drafted.</summary>
/// <param name="LocalOnly">The turn it came from held local-only memory: never put it in a prompt for a cloud model.</param>
/// <param name="Surface">The TurnState's surface, for the decider.</param>
/// <param name="Generator">The TurnState's generator description, for the decider.</param>
public sealed record LessonDraft(
    string ProjectId,
    LessonContent Content,
    bool LocalOnly = false,
    string Author = LessonAuthors.Agent,
    string Surface = "lessons",
    string Generator = "unknown");

public enum LessonFileStatus
{
    /// <summary>The draft broke the quality bar; nothing was filed.</summary>
    Invalid,
    /// <summary>Filed as a candidate, waiting in <c>/memory lessons pending</c>.</summary>
    Staged,
    /// <summary>The gate let it through: approved (and, for <c>update</c>, superseding its nearest neighbour).</summary>
    Approved,
    /// <summary>
    /// Already filed (its evidence was merged and its state left alone), or the gate chose <c>noop</c> and the candidate was
    /// rejected as a duplicate.
    /// </summary>
    Duplicate,
}

/// <param name="Superseded">The approved lesson an <c>update</c> replaced.</param>
public sealed record LessonFileResult(
    LessonFileStatus Status,
    Lesson? Lesson,
    ImmutableArray<LessonViolation> Violations,
    FabricVerdict? Decision,
    ImmutableArray<Lesson> Neighbours,
    string? Superseded = null);

/// <summary>
/// The gated write path for lessons (C-025), on the existing <c>memory_op</c> gate with lesson-shaped instructions: the
/// candidate and up to five approved lessons of the same project as neighbours. An acted <c>add</c> approves; <c>update</c>
/// approves and supersedes the nearest neighbour; <c>noop</c> rejects the candidate as a duplicate. Everything else, a
/// tainted source (plan §10.3) included, leaves it a candidate for review, so without Kev every lesson waits for
/// <c>/memory lessons</c>. Each change appends <c>MemoryWritten</c> to the source session: <c>staged</c>, <c>add</c> or
/// <c>update</c> with the lesson id.
/// </summary>
public sealed class LessonWriter
{
    private const string MemoryOp = "memory_op";
    private const int MaxNeighbours = 5;

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly LessonStore _store;
    private readonly DecisionFabric _fabric;
    private readonly IEventLog _log;
    private readonly TurnStateBuilder _states;

    public LessonWriter(LessonStore store, DecisionFabric fabric, IEventLog log, TurnStateBuilder? states = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fabric);
        ArgumentNullException.ThrowIfNull(log);
        _store = store;
        _fabric = fabric;
        _log = log;
        _states = states ?? new TurnStateBuilder();
    }

    public LessonStore Store => _store;

    /// <param name="sessionId">The session the lesson was drafted from: its decision and events are recorded there.</param>
    /// <param name="tainted">The source turn held outside content: the lesson is filed as a candidate whatever the gate would say.</param>
    public async Task<LessonFileResult> FileAsync(LessonDraft draft, string sessionId, bool tainted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!ProjectIds.IsValid(draft.ProjectId))
            throw new ArgumentException("The project id is not a 64-character lowercase hex hash.", nameof(draft));
        var content = draft.Content with
        {
            Title = draft.Content.Title.Trim(),
            Applicability = draft.Content.Applicability.Trim(),
            Body = draft.Content.Body.Trim(),
        };
        var violations = LessonValidator.Validate(content);
        if (!violations.IsEmpty)
            return new LessonFileResult(LessonFileStatus.Invalid, null, violations, null, []);

        var lesson = new Lesson(Lesson.IdFor(draft.ProjectId, content.Kind, content.Title, content.Body), draft.ProjectId, content.Kind,
            content.Title, content.Applicability, content.Body, content.Tags, LessonStates.Candidate, null, draft.Author, null, null,
            draft.LocalOnly, default, null, content.Evidence, sessionId);
        var filed = _store.File(lesson);
        // Filed before: its evidence is merged and its state stays, so a rejected or superseded lesson is never proposed again.
        if (!filed.Inserted)
            return new LessonFileResult(LessonFileStatus.Duplicate, filed.Lesson, [], null, []);
        lesson = filed.Lesson;

        if (tainted)
            return Stage(lesson, null, null, MemoryWriter.TaintedReason, []);

        var neighbours = _store.SearchApproved(draft.ProjectId, $"{content.Title} {content.Applicability} {string.Join(' ', content.Tags)}",
            MaxNeighbours).Select(hit => hit.Lesson).ToImmutableArray();
        var instructions = Instructions(lesson, neighbours);
        var state = _states.Build(new TurnStateInput(content.Title, draft.Surface, draft.Generator));
        var question = _fabric.Policy.Catalogue[MemoryOp].ToQuestion(instructions: DecisionContent.FromJson(instructions));
        var verdicts = await _fabric.DecideAsync(sessionId, Checkpoint.PreExecute, state, [KeyValuePair.Create(MemoryOp, question)],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var verdict = verdicts[MemoryOp];
        var proposed = verdict.Answer?.Choice;

        if (!verdict.Verdict.Acted || verdict.Verdict.Option is not { } operation)
            return Stage(lesson, verdict, proposed, verdict.Verdict.Reason, neighbours);
        switch (operation)
        {
            case MemoryOperations.Add:
                return Approve(lesson, verdict, proposed, null, neighbours);
            case MemoryOperations.Update when !neighbours.IsEmpty:
                return Approve(lesson, verdict, proposed, neighbours[0].LessonId, neighbours);
            case MemoryOperations.Update:
                return Stage(lesson, verdict, proposed, "no neighbour to update", neighbours);
            case MemoryOperations.Noop:
                var reason = neighbours.IsEmpty ? "duplicate (decider)" : $"duplicate of {neighbours[0].LessonId} (decider)";
                // The decider's own call: no review happened, so there is no outcome to record.
                _store.Reject(lesson.LessonId, reason, verdict.DecisionId, proposed);
                return new LessonFileResult(LessonFileStatus.Duplicate, _store.Get(lesson.LessonId), [], verdict, neighbours);
            default:
                return Stage(lesson, verdict, proposed, $"{operation} does not apply to a lesson", neighbours);
        }
    }

    /// <summary>A project's candidates, oldest first.</summary>
    public IReadOnlyList<Lesson> Pending(string projectId) => _store.List(projectId, LessonStates.Candidate);

    /// <summary>
    /// Approves a candidate after review, optionally superseding an approved lesson of the same project. Records the
    /// decision's outcome: <c>corrected</c> when the reviewer's choice (add, or update with a superseded lesson) differs
    /// from the decider's, else <c>approved</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The lesson is not a candidate, or <paramref name="supersedes"/> is not an
    /// approved lesson of the same project.</exception>
    public Lesson Approve(string lessonId, string? supersedes = null, CancellationToken cancellationToken = default)
    {
        var lesson = Candidate(lessonId);
        if (!_store.Approve(lesson.LessonId, "approved by the user", supersedes))
            throw new InvalidOperationException($"Lesson '{lessonId}' was resolved meanwhile.");
        var choice = supersedes is null ? MemoryOperations.Add : MemoryOperations.Update;
        EventAppend.Append(_log, lesson.SessionId, new MemoryWritten(lesson.LessonId, choice));
        if (lesson.DecisionId is { } decisionId)
            _fabric.RecordOutcome(lesson.SessionId, decisionId, lesson.Proposed is { } proposed && proposed != choice ? "corrected" : "approved",
                cancellationToken);
        return _store.Get(lesson.LessonId)!;
    }

    /// <summary>Rejects a candidate and records the decision's outcome as <c>rejected</c>.</summary>
    public Lesson Reject(string lessonId, string reason = "rejected by the user", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var lesson = Candidate(lessonId);
        if (!_store.Reject(lesson.LessonId, reason))
            throw new InvalidOperationException($"Lesson '{lessonId}' was resolved meanwhile.");
        if (lesson.DecisionId is { } decisionId)
            _fabric.RecordOutcome(lesson.SessionId, decisionId, "rejected", cancellationToken);
        return _store.Get(lesson.LessonId)!;
    }

    /// <summary>Marks an approved lesson superseded by another approved lesson of the same project.</summary>
    /// <param name="sessionId">Where to record the change; the newer lesson's source session when null.</param>
    public void Supersede(string oldLessonId, string newLessonId, string? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldLessonId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newLessonId);
        var replacement = _store.Get(newLessonId) ?? throw new KeyNotFoundException($"Unknown lesson '{newLessonId}'.");
        _store.Supersede(oldLessonId, newLessonId);
        EventAppend.Append(_log, sessionId ?? replacement.SessionId, new MemoryWritten(newLessonId, MemoryOperations.Update));
    }

    private Lesson Candidate(string lessonId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lessonId);
        var lesson = _store.Get(lessonId) ?? throw new KeyNotFoundException($"Unknown lesson '{lessonId}'.");
        return lesson.State == LessonStates.Candidate ? lesson
            : throw new InvalidOperationException($"Lesson '{lessonId}' is {lesson.State}, not a candidate.");
    }

    private LessonFileResult Stage(Lesson lesson, FabricVerdict? verdict, string? proposed, string reason, ImmutableArray<Lesson> neighbours)
    {
        _store.Annotate(lesson.LessonId, verdict?.DecisionId, proposed, reason);
        EventAppend.Append(_log, lesson.SessionId, new MemoryWritten(lesson.LessonId, MemoryOperations.Staged));
        return new LessonFileResult(LessonFileStatus.Staged, _store.Get(lesson.LessonId), [], verdict, neighbours);
    }

    private LessonFileResult Approve(Lesson lesson, FabricVerdict verdict, string? proposed, string? supersedes, ImmutableArray<Lesson> neighbours)
    {
        if (!_store.Approve(lesson.LessonId, "approved by the decider", supersedes, verdict.DecisionId, proposed))
            throw new InvalidOperationException($"Lesson '{lesson.LessonId}' was resolved meanwhile.");
        EventAppend.Append(_log, lesson.SessionId, new MemoryWritten(lesson.LessonId, supersedes is null ? MemoryOperations.Add : MemoryOperations.Update));
        return new LessonFileResult(LessonFileStatus.Approved, _store.Get(lesson.LessonId), [], verdict, neighbours, supersedes);
    }

    private JsonElement Instructions(Lesson candidate, ImmutableArray<Lesson> neighbours)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("question", "How should this lesson change memory, given the project's approved lessons? "
                + "add: a new lesson; update: replaces the first neighbour; noop: already known; invalidate does not apply.");
            writer.WritePropertyName("candidate");
            Write(writer, candidate, body: true);
            writer.WriteStartArray("neighbours");
            foreach (var neighbour in neighbours)
                Write(writer, neighbour, body: false);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();

        static void Write(Utf8JsonWriter writer, Lesson lesson, bool body)
        {
            writer.WriteStartObject();
            writer.WriteString("id", lesson.LessonId);
            writer.WriteString("kind", lesson.Kind);
            writer.WriteString("title", lesson.Title);
            writer.WriteString("applicability", lesson.Applicability);
            if (body)
                writer.WriteString("body", lesson.Body);
            writer.WriteStartArray("tags");
            foreach (var tag in lesson.Tags)
                writer.WriteStringValue(tag);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }
}

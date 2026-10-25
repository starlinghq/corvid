using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.IO;

namespace Corvid.Approvals;

/// <summary>How long an answer lasts (plan §10.2 step 5).</summary>
public enum ApprovalScope
{
    /// <summary>This action only.</summary>
    Once,
    /// <summary>Matching actions for the rest of the session.</summary>
    Session,
    /// <summary>Matching actions in every session, until the rule is removed.</summary>
    Always,
}

public enum RuleDecision
{
    Allow,
    Deny,
}

/// <summary>
/// A remembered answer, stored as structure rather than a command string. An allow rule matches a command with the same
/// verb and subcommand whose effects are a subset of the rule's, whose elevation is the same, whose every path is at or
/// under one of the rule's paths (or matched by one of its patterns), and whose every host and process is listed: approving
/// <c>rm -rf build</c> never matches <c>rm -rf /</c>, and approving <c>rm *.o</c> never matches <c>rm main.c</c>. A deny rule matches the same verb and subcommand touching any of its paths or names (or any
/// at all when it lists none). Commands with anything unresolved never match an allow rule.
/// </summary>
/// <param name="Kind"><c>shell</c> or <c>tool</c>.</param>
/// <param name="Verb">The program or tool name.</param>
/// <param name="Paths">Absolute paths, where a target matches at or below one, or patterns, which match what bash would.</param>
/// <param name="Names">Hosts and processes as <c>host:example.org</c>, <c>process:1234</c>, <c>process_name:node</c>.</param>
/// <param name="Source">The action text the rule was made from, for display.</param>
public sealed record ApprovalRule(
    RuleDecision Decision,
    string Kind,
    string Verb,
    string? Subcommand,
    ActionEffects Effects,
    bool Elevated,
    ImmutableArray<string> Paths,
    ImmutableArray<string> Names,
    string Source,
    DateTimeOffset CreatedAt)
{
    /// <summary>The rule a user's answer creates for one command of an action.</summary>
    public static ApprovalRule From(RuleDecision decision, string kind, AnalyzedCommand command, string source, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new ApprovalRule(decision, kind, command.Verb, command.Subcommand, command.Effects & ~ActionEffects.Elevated,
            command.Effects.HasFlag(ActionEffects.Elevated),
            [.. command.Targets.Where(target => target.Kind == TargetKind.Path).Select(target => target.Value).Distinct()],
            [.. command.Targets.Where(target => target.Kind is not (TargetKind.Path or TargetKind.Url)).Select(Name).Distinct()],
            source, createdAt);
    }

    /// <summary>The rule in words, as an approval card says it: "`rm -r` under /work/build", with code in backticks.</summary>
    public string Describe() =>
        $"`{(Subcommand is { Length: > 0 } sub ? $"{Verb} {sub}" : Verb)}`"
        + (Paths.IsEmpty ? "" : " under " + string.Join(", ", Paths))
        + (Names.IsEmpty ? "" : " for " + string.Join(", ", Names));

    public bool Allows(string kind, AnalyzedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Decision != RuleDecision.Allow || !SameCommand(kind, command) || !command.Unresolved.IsEmpty)
            return false;
        if (command.Effects.HasFlag(ActionEffects.Elevated) != Elevated
            || (command.Effects & ~ActionEffects.Elevated & ~Effects) != ActionEffects.None)
            return false;
        foreach (var target in command.Targets)
        {
            if (target.Dynamic || target.Kind == TargetKind.Path && target.Location is PathLocation.Unresolved or null)
                return false;
            if (target.Kind == TargetKind.Path && !Paths.Any(path => Covers(path, target)))
                return false;
            if (target.Kind is not (TargetKind.Path or TargetKind.Url) && !Names.Contains(Name(target)))
                return false;
        }

        return true;
    }

    public bool Denies(string kind, AnalyzedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Decision != RuleDecision.Deny || !SameCommand(kind, command))
            return false;
        if (Paths.IsEmpty && Names.IsEmpty)
            return true;
        return command.Targets.Any(target =>
            target.Kind == TargetKind.Path && Paths.Select(path => IsPattern(path) ? StaticDirectory(path) : path)
                .Any(path => PathMath.IsAtOrUnder(PathScope(target), path) || PathMath.IsUnder(path, PathScope(target)))
            || target.Kind is not (TargetKind.Path or TargetKind.Url) && Names.Contains(Name(target)));
    }

    // Records compare ImmutableArray fields by reference; rules are equal when their contents are.
    public bool Equals(ApprovalRule? other) =>
        other is not null && Decision == other.Decision && Kind == other.Kind && Verb == other.Verb && Subcommand == other.Subcommand
        && Effects == other.Effects && Elevated == other.Elevated && Paths.SequenceEqual(other.Paths) && Names.SequenceEqual(other.Names)
        && Source == other.Source && CreatedAt == other.CreatedAt;

    public override int GetHashCode() => HashCode.Combine(Decision, Kind, Verb, Subcommand, Effects, Elevated, Source, CreatedAt);

    private bool SameCommand(string kind, AnalyzedCommand command) =>
        Kind == kind && Verb == command.Verb && Subcommand == command.Subcommand;

    // An approved path covers itself and everything under it. An approved pattern covers only that pattern and the paths
    // it matches, so approving "rm *.o" never covers "rm main.c" in the same directory.
    private static bool Covers(string path, ActionTarget target)
    {
        if (!IsPattern(path))
            return PathMath.IsAtOrUnder(PathScope(target), path);
        if (target.Pattern)
            return target.Value == path;
        var parts = PathMath.Components(path);
        var names = PathMath.Components(target.Value);
        return parts.Length == names.Length
            && parts.Zip(names).All(pair => ProtectedPathMatch.Component(pair.First, pair.Second, dotglob: false));
    }

    private static bool IsPattern(string path) => path.AsSpan().IndexOfAny("*?[") >= 0;

    // For a denial, a pattern is scoped by its static directory.
    private static string PathScope(ActionTarget target) => target.Pattern ? StaticDirectory(target.Value) : target.Value;

    private static string StaticDirectory(string path)
    {
        var glob = path.AsSpan().IndexOfAny("*?[");
        var slash = glob < 0 ? path.Length : path.LastIndexOf('/', glob);
        return slash <= 0 ? "/" : path[..slash];
    }

    private static string Name(ActionTarget target) => $"{WireNames.Kind(target.Kind)}:{target.Value}";
}

/// <summary>Where "always" rules live. Session rules are held by the pipeline for the session's lifetime.</summary>
public interface IApprovalRuleStore
{
    ImmutableArray<ApprovalRule> Rules { get; }

    /// <summary>Stores the rules; a rule equal to one stored already (the same content and time) is not stored again.</summary>
    void Add(IEnumerable<ApprovalRule> rules);

    /// <summary>
    /// Removes a remembered rule (K-7): the stored rule equal to <paramref name="rule"/>. <see cref="Add"/> stores an equal rule
    /// once, so this is one rule; a file written by hand with exact duplicates loses all of them, which read as one rule anyway.
    /// Calls it allowed ask again from the next one. False when no such rule is stored (already removed), which changes nothing.
    /// </summary>
    bool Remove(ApprovalRule rule);
}

public sealed class InMemoryApprovalRuleStore : IApprovalRuleStore
{
    private ImmutableArray<ApprovalRule> _rules = [];

    public ImmutableArray<ApprovalRule> Rules => _rules;

    public void Add(IEnumerable<ApprovalRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ImmutableInterlocked.Update(ref _rules, (current, added) => added.Aggregate(current, (all, rule) => all.Contains(rule) ? all : all.Add(rule)),
            rules.ToArray());
    }

    public bool Remove(ApprovalRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var removed = false;
        ImmutableInterlocked.Update(ref _rules, current =>
        {
            var kept = current.RemoveAll(stored => stored.Equals(rule));
            removed = kept.Length != current.Length;
            return kept;
        });
        return removed;
    }
}

/// <summary>
/// "Always" rules in a JSON file (format <c>corvid-approval-rules</c>, version 1), shared by every Corvid process on the
/// machine: the CLI, the app and a gateway may each hold a store on the same file. The default location,
/// <c>$XDG_CONFIG_HOME/corvid/approvals.json</c>, is inside Corvid's safety configuration, which the hardline list
/// protects from every tool.
/// <para>
/// Each change takes a cross-process lock (<c>approvals.json.lock</c>, opened with no sharing), reads the file again under
/// it, applies only its own delta (these rules added, this rule removed) and replaces the file through a temporary file of
/// its own: two processes never undo or resurrect each other's rules, nor collide on the temporary file.
/// <see cref="Rules"/> reads the file again whenever it changed on disk since this store last saw it, so a rule another
/// process added or removed applies here from the next call.
/// </para>
/// </summary>
public sealed class JsonFileApprovalRuleStore : IApprovalRuleStore
{
    public const string Format = "corvid-approval-rules";
    public const int FormatVersion = 1;

    /// <summary>How long a change waits for another process's change to finish before it gives up.</summary>
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(10);

    private readonly string _path;
    private readonly string _lockPath;
    private readonly TimeSpan _lockTimeout;
    private readonly Lock _gate = new();
    private ImmutableArray<ApprovalRule> _rules;
    // The file as _rules was last read or written: its last write time and length, or null when it did not exist.
    private (DateTime Written, long Length)? _seen;

    public JsonFileApprovalRuleStore(string path)
        : this(path, DefaultLockTimeout)
    {
    }

    internal JsonFileApprovalRuleStore(string path, TimeSpan lockTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _lockPath = LockPath(_path);
        _lockTimeout = lockTimeout;
        lock (_gate)
        {
            _rules = ReadLocked();
        }
    }

    /// <summary>Test seam: called each time a change finds the lock held by another handle, before it waits again.</summary>
    internal Action? Waiting { get; set; }

    /// <summary>The lock file every store on <paramref name="path"/> takes for a change.</summary>
    internal static string LockPath(string path) => Path.GetFullPath(path) + ".lock";

    public static string DefaultPath(ApprovalEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var config = environment.Variables.TryGetValue("XDG_CONFIG_HOME", out var value) ? value : Path.Combine(environment.Home, ".config");
        return Path.Combine(config, "corvid", "approvals.json");
    }

    /// <summary>The stored rules, read again first when the file changed on disk since this store last read or wrote it.</summary>
    public ImmutableArray<ApprovalRule> Rules
    {
        get
        {
            lock (_gate)
            {
                if (Stamp() != _seen)
                    _rules = ReadLocked();
                return _rules;
            }
        }
    }

    /// <summary>Adds the rules not stored already (an equal rule is stored once) to the file as it is on disk now.</summary>
    /// <exception cref="TimeoutException">Another process held the file's lock past the lock timeout; nothing changed.</exception>
    public void Add(IEnumerable<ApprovalRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var added = rules.ToArray();
        // The file lock first, outside the gate: a wait on another process's change never holds up a rule check here.
        using var held = AcquireFileLock();
        lock (_gate)
        {
            var current = ReadLocked();
            var updated = current;
            foreach (var rule in added)
                if (!updated.Contains(rule))
                    updated = updated.Add(rule);
            if (updated.Length != current.Length)
                WriteLocked(updated);
        }
    }

    /// <summary>
    /// Removes the rule from the file as it is on disk now and replaces the file atomically. A rule not in the file (removed
    /// already, here or by another process) leaves it untouched and returns false.
    /// </summary>
    /// <exception cref="TimeoutException">Another process held the file's lock past the lock timeout; nothing changed.</exception>
    public bool Remove(ApprovalRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        using var held = AcquireFileLock();
        lock (_gate)
        {
            var current = ReadLocked();
            var kept = current.RemoveAll(stored => stored.Equals(rule));
            if (kept.Length == current.Length)
                return false;
            WriteLocked(kept);
            return true;
        }
    }

    /// <summary>
    /// The cross-process lock: the lock file opened with no sharing, which .NET takes as an exclusive <c>flock</c> on Unix
    /// (and a share-none open on Windows), so it excludes another store on the same file in this process too. Only a lock
    /// another handle holds is waited on; a lock file that cannot be opened (a read-only directory or file system) refuses at
    /// once and says why.
    /// </summary>
    /// <exception cref="TimeoutException">Another process held the lock past the lock timeout.</exception>
    /// <exception cref="UnauthorizedAccessException">The lock file cannot be opened, with why.</exception>
    private FileStream AcquireFileLock()
    {
        var deadline = Environment.TickCount64 + (long)_lockTimeout.TotalMilliseconds;
        var wait = 2;
        while (true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException exception) when (FileLocks.IsHeldElsewhere(exception) && Environment.TickCount64 < deadline)
            {
                Waiting?.Invoke();
                Thread.Sleep(wait);
                wait = Math.Min(wait * 2, 100);
            }
            catch (IOException exception) when (FileLocks.IsHeldElsewhere(exception))
            {
                throw new TimeoutException(
                    $"Another Corvid process held {_lockPath} for over {_lockTimeout.TotalSeconds:0.#} seconds; the approval rules were not changed.",
                    exception);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new UnauthorizedAccessException(
                    $"The approval rules were not changed: {FileLocks.Unopenable(_lockPath, exception)}.", exception);
            }
        }
    }

    // Under the gate: the file's rules, noting the file as read. A file that does not exist holds none.
    private ImmutableArray<ApprovalRule> ReadLocked()
    {
        var stamp = Stamp();
        string text;
        try
        {
            text = File.ReadAllText(_path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            _seen = null;
            return [];
        }
        var rules = Parse(text);
        _seen = stamp;
        return rules;
    }

    // Under the gate and the file lock: a temporary file of this call's own, renamed over the file, then the list, so a failed
    // write leaves both as they were. The write time is moved past the replaced file's, so a store that read that file sees
    // a change even when both writes fall inside one tick of the file system's clock.
    private void WriteLocked(ImmutableArray<ApprovalRule> updated)
    {
        var previous = Stamp()?.Written;
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, Serialize(updated));
            File.Move(temporary, _path, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
        var written = DateTime.UtcNow;
        if (previous is { } before && written <= before)
            written = before.AddTicks(TimeSpan.TicksPerMillisecond);
        File.SetLastWriteTimeUtc(_path, written);
        _rules = updated;
        _seen = Stamp();
    }

    private (DateTime Written, long Length)? Stamp()
    {
        var info = new FileInfo(_path);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
    }

    public static string Serialize(IEnumerable<ApprovalRule> rules) => JsonSerializer.Serialize(
        new RuleFileDocument(Format, FormatVersion, [.. rules.Select(rule => new RuleDocument(
            rule.Decision == RuleDecision.Allow ? "allow" : "deny", rule.Kind, rule.Verb, rule.Subcommand,
            WireNames.Effects(rule.Effects), rule.Elevated, [.. rule.Paths], [.. rule.Names], rule.Source, rule.CreatedAt))]),
        ApprovalJsonContext.Default.RuleFileDocument);

    public static ImmutableArray<ApprovalRule> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        RuleFileDocument? file;
        try
        {
            file = JsonSerializer.Deserialize(json, ApprovalJsonContext.Default.RuleFileDocument);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid approval rule file.", exception);
        }

        if (file is null || file.Format != Format || file.FormatVersion != FormatVersion)
            throw new InvalidDataException($"Not a {Format} file of version {FormatVersion}.");
        return [.. file.Rules.Select(rule => new ApprovalRule(
            rule.Decision switch
            {
                "allow" => RuleDecision.Allow,
                "deny" => RuleDecision.Deny,
                _ => throw new InvalidDataException($"Unknown rule decision '{rule.Decision}'."),
            },
            rule.Kind is "shell" or "tool" ? rule.Kind : throw new InvalidDataException($"Unknown rule kind '{rule.Kind}'."),
            rule.Verb, rule.Subcommand, WireNames.Effects(rule.Effects), rule.Elevated,
            [.. rule.Paths.Select(path => Path.IsPathFullyQualified(path) ? PathMath.Normalize(path)
                : throw new InvalidDataException($"Rule path '{path}' is not absolute."))],
            [.. rule.Names], rule.Source, rule.CreatedAt))];
    }
}

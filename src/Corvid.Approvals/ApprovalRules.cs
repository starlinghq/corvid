using System.Collections.Immutable;
using System.Text.Json;

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

    void Add(IEnumerable<ApprovalRule> rules);

    /// <summary>
    /// Removes a remembered rule (K-7): every stored rule equal to <paramref name="rule"/>. Calls it allowed ask again from
    /// the next one. False when no such rule is stored (already removed), which changes nothing.
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
        ImmutableInterlocked.Update(ref _rules, (current, added) => current.AddRange(added), rules.ToArray());
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
/// "Always" rules in a JSON file (format <c>corvid-approval-rules</c>, version 1), written atomically. The default location,
/// <c>$XDG_CONFIG_HOME/corvid/approvals.json</c>, is inside Corvid's safety configuration, which the hardline list
/// protects from every tool.
/// </summary>
public sealed class JsonFileApprovalRuleStore : IApprovalRuleStore
{
    public const string Format = "corvid-approval-rules";
    public const int FormatVersion = 1;

    private readonly string _path;
    private readonly Lock _gate = new();
    private ImmutableArray<ApprovalRule> _rules;

    public JsonFileApprovalRuleStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _rules = File.Exists(_path) ? Parse(File.ReadAllText(_path)) : [];
    }

    public static string DefaultPath(ApprovalEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var config = environment.Variables.TryGetValue("XDG_CONFIG_HOME", out var value) ? value : Path.Combine(environment.Home, ".config");
        return Path.Combine(config, "corvid", "approvals.json");
    }

    public ImmutableArray<ApprovalRule> Rules => _rules;

    public void Add(IEnumerable<ApprovalRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        lock (_gate)
        {
            Write(_rules.AddRange(rules));
        }
    }

    /// <summary>Removes the rule and rewrites the file atomically; a rule not in the file leaves it untouched.</summary>
    public bool Remove(ApprovalRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (_gate)
        {
            var kept = _rules.RemoveAll(stored => stored.Equals(rule));
            if (kept.Length == _rules.Length)
                return false;
            Write(kept);
            return true;
        }
    }

    // Under the gate: the file first, then the list, so a failed write leaves both as they were.
    private void Write(ImmutableArray<ApprovalRule> updated)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, Serialize(updated));
        File.Move(temporary, _path, overwrite: true);
        _rules = updated;
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

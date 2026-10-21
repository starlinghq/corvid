using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using Corvid.Abstractions.Events;

namespace Corvid.Rules;

/// <summary>
/// Reads and validates one rule file (C-023). Everything is checked at load: the fields and their values, exactly one of
/// <c>match</c> or <c>correlation</c>, every field path against the event types, every regex under NonBacktracking, and
/// the embedded tests (parsed as session events) against the rule's maturity.
/// </summary>
public static partial class RuleLoader
{
    /// <summary>A correlation has at least two steps and at most this many.</summary>
    public const int MaxSteps = 8;

    /// <summary>Embedded test events with no <c>session</c> belong to this one.</summary>
    public const string DefaultSession = "s1";

    /// <summary>The time embedded test offsets count from.</summary>
    public static readonly DateTimeOffset TestEpoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan MaxWindow = TimeSpan.FromDays(7);

    /// <param name="file">The file name (<c>&lt;id&gt;.rule.json</c>) or another label for messages.</param>
    public static DetectionRule Parse(string file, string json)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(json);
        RuleFile document;
        try
        {
            document = JsonSerializer.Deserialize(json, RuleFileJsonContext.Default.RuleFile) ?? throw new RuleLoadException(file, "the file is empty");
        }
        catch (JsonException exception)
        {
            throw new RuleLoadException(file, "not a valid rule file: " + exception.Message, exception);
        }
        try
        {
            return Build(file, document);
        }
        catch (FormatException exception)
        {
            throw new RuleLoadException(file, exception.Message, exception);
        }
    }

    private static DetectionRule Build(string file, RuleFile document)
    {
        if (!IdPattern().IsMatch(document.Id))
            throw new FormatException($"id '{document.Id}' must match ^[a-z0-9][a-z0-9-]*$");
        var name = Path.GetFileName(file);
        if (name.EndsWith(".rule.json", StringComparison.Ordinal) && name != document.Id + ".rule.json")
            throw new FormatException($"the file of rule '{document.Id}' must be named {document.Id}.rule.json");
        if (document.Version < 1)
            throw new FormatException("version must be at least 1");
        Required(document.Title, "title");
        Required(document.Reason, "reason");
        Required(document.Category, "category");
        if (RuleSeverity.Rank(document.Severity) < 0)
            throw new FormatException($"severity '{document.Severity}' must be one of {string.Join(", ", RuleSeverity.All)}");
        if (document.Status is not (RuleStatus.Experimental or RuleStatus.Stable or RuleStatus.Deprecated))
            throw new FormatException($"status '{document.Status}' must be experimental, stable or deprecated");
        if (document.Posture is not (RulePosture.Detect or RulePosture.Enforce))
            throw new FormatException($"posture '{document.Posture}' must be detect or enforce");
        switch (document.Enforce)
        {
            case null:
                break;
            case "ask" when document.Posture == RulePosture.Enforce:
                break;
            case "ask":
                throw new FormatException("enforce applies only to posture enforce");
            case "deny":
                throw new FormatException("enforce 'deny' is not supported: an enforce rule forces an approval prompt (ask); a denial needs its own approval resolution, which is a C-023 follow-up");
            default:
                throw new FormatException($"enforce '{document.Enforce}' must be ask");
        }

        RuleStep? match = null;
        RuleCorrelation? correlation = null;
        if ((document.Match is null) == (document.Correlation is null))
            throw new FormatException("a rule has exactly one of match or correlation");
        if (document.Match is { } single)
            match = Step(single, "match", "match");
        else
            correlation = Correlation(document.Correlation!);

        var tests = document.Tests.IsDefault ? [] : document.Tests.Select((test, i) => Test(test, i)).ToImmutableArray();
        switch (document.Status)
        {
            case RuleStatus.Experimental when tests.IsEmpty:
                throw new FormatException("an experimental rule needs at least one test");
            case RuleStatus.Stable when !tests.Any(test => test.ExpectMatch) || !tests.Any(test => !test.ExpectMatch):
                throw new FormatException("a stable rule needs at least one match test and one no_match test");
        }
        var names = tests.GroupBy(test => test.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (names is not null)
            throw new FormatException($"test name '{names.Key}' is used twice");

        return new DetectionRule(document.Id, document.Version, document.Title, document.Description, document.Severity, document.Status,
            document.Posture, document.Reason, document.Category, match, correlation, tests);
    }

    private static void Required(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException($"{field} must not be empty");
    }

    private static RuleStep Step(StepFile step, string id, string at)
    {
        if (step.Event == "detection_matched")
            throw new FormatException($"{at}: rules may not select detection_matched, so a detection never feeds another");
        if (!EventFields.Events.ContainsKey(step.Event))
            throw new FormatException($"{at}: unknown event '{step.Event}' (one of {string.Join(", ", EventFields.Events.Keys.Order(StringComparer.Ordinal))})");
        var where = step.Where is { } predicate ? Predicate.Parse(predicate, step.Event, at + ".where") : Predicate.True;
        return new RuleStep(step.Id ?? id, step.Event, where);
    }

    private static RuleCorrelation Correlation(CorrelationFile correlation)
    {
        if (correlation.Scope != "session")
            throw new FormatException($"correlation.scope '{correlation.Scope}' must be session");
        var sequence = correlation.Order switch
        {
            null or "sequence" => true,
            "any" => false,
            var other => throw new FormatException($"correlation.order '{other}' must be sequence or any"),
        };
        var window = Window(correlation.Window);
        var steps = correlation.Steps.IsDefault ? [] : correlation.Steps;
        if (steps.Length is < 2 or > MaxSteps)
            throw new FormatException($"a correlation has 2 to {MaxSteps} steps, not {steps.Length}");
        var built = steps.Select((step, i) => Step(step, $"step{i + 1}", $"correlation.steps[{i}]")).ToImmutableArray();
        var duplicate = built.GroupBy(step => step.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new FormatException($"step id '{duplicate.Key}' is used twice");
        return new RuleCorrelation(window, sequence, built);
    }

    /// <summary><c>90s</c>, <c>5m</c>, <c>2h</c>, <c>1d</c>, or an ISO-8601 duration (<c>PT10M</c>).</summary>
    internal static TimeSpan Window(string text)
    {
        TimeSpan window;
        if (WindowPattern().Match(text) is { Success: true } parsed)
        {
            var amount = long.Parse(parsed.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            window = parsed.Groups[2].Value switch
            {
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "h" => TimeSpan.FromHours(amount),
                _ => TimeSpan.FromDays(amount),
            };
        }
        else
        {
            try
            {
                window = text.StartsWith('P') ? XmlConvert.ToTimeSpan(text) : throw new FormatException();
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                throw new FormatException($"correlation.window '{text}' must be a duration such as 120s, 5m or PT10M");
            }
        }
        if (window <= TimeSpan.Zero || window > MaxWindow)
            throw new FormatException($"correlation.window '{text}' must be positive and at most 7 days");
        return window;
    }

    private static RuleTest Test(RuleTestFile test, int index)
    {
        var at = $"tests[{index}]";
        Required(test.Name, at + ".name");
        var expect = test.Verdict switch
        {
            "match" => true,
            "no_match" => false,
            var other => throw new FormatException($"{at}.verdict '{other}' must be match or no_match"),
        };
        if (test.Events.IsDefaultOrEmpty)
            throw new FormatException($"{at} ('{test.Name}') has no events");
        var events = ImmutableArray.CreateBuilder<StoredEvent>();
        var sequences = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < test.Events.Length; i++)
        {
            var where = $"{at}.events[{i}] ('{test.Name}')";
            if (JsonNode.Parse(test.Events[i].GetRawText()) is not JsonObject node)
                throw new FormatException($"{where} must be an object");
            var offset = (double)i;
            if (node.Remove("at", out var atNode))
                offset = atNode is JsonValue value && value.TryGetValue<double>(out var seconds) && seconds >= 0
                    ? seconds
                    : throw new FormatException($"{where}.at must be a non-negative number of seconds");
            var session = DefaultSession;
            if (node.Remove("session", out var sessionNode))
                session = sessionNode is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
                    ? text
                    : throw new FormatException($"{where}.session must be a non-empty string");
            SessionEvent payload;
            try
            {
                payload = node.Deserialize(RuleFixtureJsonContext.Default.SessionEvent) ?? throw new FormatException($"{where} is null");
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                throw new FormatException($"{where} is not a session event in the persisted form: {exception.Message}");
            }
            var sequence = sequences[session] = sequences.GetValueOrDefault(session) + 1;
            events.Add(new StoredEvent(session, sequence, Guid.Empty, TestEpoch + TimeSpan.FromSeconds(offset), payload));
        }
        return new RuleTest(test.Name, expect, events.ToImmutable());
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^([0-9]{1,9})(s|m|h|d)$")]
    private static partial Regex WindowPattern();
}

/// <summary>A set of rules with unique ids. <see cref="BuiltIn"/> is the pack shipped inside this assembly.</summary>
public sealed class RulePack
{
    private const string ResourcePrefix = "Corvid.Rules.Pack.";
    private static readonly Lazy<RulePack> Shipped = new(LoadBuiltIn);

    private RulePack(ImmutableArray<DetectionRule> rules) => Rules = rules;

    public static RulePack Empty { get; } = new([]);

    /// <summary>Rules already parsed (tests); ids are not checked.</summary>
    internal static RulePack Of(params DetectionRule[] rules) => new([.. rules]);

    public ImmutableArray<DetectionRule> Rules { get; }

    public DetectionRule? Find(string id) => Rules.FirstOrDefault(rule => rule.Id == id);

    /// <summary>Loads rule files (name and contents). Any invalid file, or two rules with one id, fails the whole pack.</summary>
    public static RulePack Load(IEnumerable<KeyValuePair<string, string>> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var rules = ImmutableArray.CreateBuilder<DetectionRule>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (file, json) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var rule = RuleLoader.Parse(file, json);
            if (!seen.TryAdd(rule.Id, file))
                throw new RuleLoadException(file, $"rule id '{rule.Id}' is already used by {seen[rule.Id]}");
            rules.Add(rule);
        }
        return new RulePack(rules.ToImmutable());
    }

    /// <summary>The built-in pack (embedded <c>Pack/&lt;category&gt;/&lt;id&gt;.rule.json</c>), loaded once.</summary>
    public static RulePack BuiltIn() => Shipped.Value;

    /// <summary>The built-in rule files by name, for tests and tooling.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> BuiltInFiles()
    {
        var assembly = typeof(RulePack).Assembly;
        var files = new List<KeyValuePair<string, string>>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            files.Add(KeyValuePair.Create(resource[ResourcePrefix.Length..], reader.ReadToEnd()));
        }
        return files;
    }

    private static RulePack LoadBuiltIn() => Load(BuiltInFiles());
}

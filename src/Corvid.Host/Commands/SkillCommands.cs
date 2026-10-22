using System.Globalization;
using Corvid.Host.Configuration;
using Corvid.Skills;

namespace Corvid.Host.Commands;

/// <summary>
/// <c>/skills</c> (plan §11, §17 row 1.3): list, show, review the learning loop's staged drafts, history (<c>log</c>,
/// <c>diff</c>, <c>revert</c> through git), import from a folder or from Hermes (D-9), evals, pins and the curator's stale
/// rule. Every subcommand is the user acting, so changes it makes are authored <c>user</c> (an import is <c>hub:&lt;source&gt;</c>,
/// an approved draft stays <c>agent</c>).
/// </summary>
public static class SkillCommands
{
    public const string Usage =
        "[list] | show <name> | pending | approve <id> | reject <id> | log [name] | diff [rev] | revert <rev> | " +
        "import <folder> [source] | import hermes [<hermes home>] | evals <name> | pin <name> | unpin <name> | stale [days] [--archive]";

    internal static async ValueTask<CommandOutput> RunAsync(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        var skills = context.Runtime.Skills;
        if (skills.Store is not { } store)
            return CommandOutput.Warning($"Skills are {skills.State}.");
        try
        {
            switch (invocation.Arguments)
            {
                case [] or ["list"]:
                    await store.RefreshAsync(cancellationToken).ConfigureAwait(false);
                    return List(store, skills);
                case ["show", var name]:
                    return Show(store, name);
                case ["pending"]:
                    return Pending(skills);
                case ["approve", var id]:
                    if (skills.Learner is not { } approver)
                        return CommandOutput.Warning("The learning loop is off (Corvid:Skills:Learning).");
                    var approved = await approver.ApproveAsync(id, context.Session.Id, cancellationToken).ConfigureAwait(false);
                    return CommandOutput.Success($"Approved {id}{(approved is null ? " (no change)" : $": committed {Short(approved)} as agent")}.");
                case ["reject", var id]:
                    if (skills.Learner is not { } rejecter)
                        return CommandOutput.Warning("The learning loop is off (Corvid:Skills:Learning).");
                    rejecter.Reject(id, cancellationToken);
                    return CommandOutput.Success($"Rejected {id}.");
                case ["log", .. var rest] when rest.Length <= 1:
                    return await LogAsync(store, rest.FirstOrDefault(), cancellationToken).ConfigureAwait(false);
                case ["diff", .. var rest] when rest.Length <= 1:
                    return CommandOutput.Text(await store.Repository.ShowAsync(rest.FirstOrDefault() ?? "HEAD", cancellationToken: cancellationToken).ConfigureAwait(false));
                case ["revert", var revision]:
                    var reverted = await store.RevertAsync(revision, context.Session.Id, cancellationToken).ConfigureAwait(false);
                    return CommandOutput.Success($"Reverted {revision} as user: {Short(reverted)}. The files it touched are now the user's.");
                case ["import", "hermes", .. var hermesHome] when hermesHome.Length <= 1:
                    // The same Hermes home, sticky profile included, as /memory import hermes (C-035). A folder named hermes is ./hermes.
                    var location = HermesHome.Resolve(CorvidPaths.Home, hermesHome.FirstOrDefault() is { } given ? CorvidPaths.ExpandHome(given) : null);
                    if (location.Home is not { } resolved)
                        return CommandOutput.Warning(location.Problem + " Usage: /skills import hermes <hermes home>.");
                    return Imported(await store.ImportAsync(Path.Combine(resolved, "skills"), SkillAuthor.Hub("hermes"),
                        context.Session.Id, cancellationToken).ConfigureAwait(false), "hub:hermes");
                case ["import", var folder, .. var source] when source.Length <= 1:
                    var author = SkillAuthor.Hub(source.FirstOrDefault() ?? "local");
                    return Imported(await store.ImportAsync(CorvidPaths.ExpandHome(folder), author, context.Session.Id, cancellationToken)
                        .ConfigureAwait(false), author.Name);
                case ["evals", var name]:
                    if (skills.Curator is not { } curator)
                        return CommandOutput.Warning("Skill evals need the learning services, which are not running.");
                    var report = await curator.RunEvalsAsync(name, cancellationToken).ConfigureAwait(false);
                    return report is null ? CommandOutput.Text($"{name} has no evals/ cases.") : new CommandOutput
                    {
                        Title = $"Evals for {name} at {Short(report.Revision)}: {report.Passed}/{report.Cases.Length} passed",
                        Tone = report.Passed == report.Cases.Length ? OutputTone.Success : OutputTone.Warning,
                        Table = CommandTable.Of(["case", "result"], report.Cases.Select(item => new[] { item.Case.Id, item.Outcome.Describe() })),
                    };
                case [var verb, var name] when verb is "pin" or "unpin":
                    if (!store.TryGet(name, out _))
                        return CommandOutput.Error($"There is no skill named '{name}'.");
                    skills.Database!.SetPinned(name, verb == "pin");
                    return CommandOutput.Success(verb == "pin" ? $"{name} is pinned: the curator never touches it." : $"{name} is unpinned.");
                case ["stale", .. var rest] when rest.Length <= 2:
                    return await StaleAsync(context, skills, rest, cancellationToken).ConfigureAwait(false);
                default:
                    return CommandOutput.Error("Usage: /skills " + Usage);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or SkillOwnershipException or GitException or SkillFormatException
                                              or InvalidOperationException or IOException)
        {
            return CommandOutput.Error(exception.Message);
        }
    }

    private static CommandOutput List(SkillStore store, Composition.SkillServices skills)
    {
        var lines = new List<string> { $"{store.Skills.Count} skill(s) in {store.Root}." };
        if (skills.Database?.Pending() is { Count: > 0 } pending)
            lines.Add($"{pending.Count} draft(s) wait for review: /skills pending.");
        lines.AddRange(store.Issues.Select(issue => $"not loaded: {issue.Path}: {issue.Problem}"));
        return new CommandOutput
        {
            Title = "Skills",
            Lines = [.. lines],
            Table = store.Skills.Count == 0 ? null : CommandTable.Of(["name", "author", "evals", "description"], store.Skills.Select(skill => new[]
            {
                skill.Name + (skills.Database?.Usage(skill.Name).Pinned == true ? " (pinned)" : ""),
                (skill.Author?.Name ?? "uncommitted") + (skill.AgentOwned ? "" : skill.Trusted ? "" : ", outside content"),
                skill.Evals.IsEmpty ? "" : skills.Database?.LastEval(skill.Name) is { } eval ? $"{eval.Passed}/{eval.Total}" : $"{skill.Evals.Length} case(s)",
                skill.Document.Description,
            })),
        };
    }

    private static CommandOutput Show(SkillStore store, string name)
    {
        if (!store.TryGet(name, out var skill))
            return CommandOutput.Error($"There is no skill named '{name}'.");
        var activation = skill.Document.Activation;
        var lines = new List<string>
        {
            skill.Document.Description,
            $"folder: {skill.Path}; author: {skill.Author?.Name ?? "uncommitted"}{(skill.AgentOwned ? " (the agent may change it)" : "")}" +
            (skill.Trusted ? "" : "; from a hub: loading it taints the session"),
        };
        if (!activation.Platforms.IsDefaultOrEmpty)
            lines.Add("platforms: " + string.Join(", ", activation.Platforms));
        foreach (var (label, values) in new[]
                 {
                     ("requires toolsets", activation.RequiresToolsets), ("requires tools", activation.RequiresTools),
                     ("fallback for toolsets", activation.FallbackForToolsets), ("fallback for tools", activation.FallbackForTools),
                 })
            if (!values.IsDefaultOrEmpty)
                lines.Add($"{label}: {string.Join(", ", values)}");
        if (!skill.Resources.IsEmpty)
            lines.Add("files: " + string.Join(", ", skill.Resources));
        lines.Add("");
        lines.AddRange(skill.Document.Body.Trim().Split('\n'));
        return new CommandOutput { Title = skill.Name, Lines = [.. lines] };
    }

    private static CommandOutput Pending(Composition.SkillServices skills)
    {
        var pending = skills.Database?.Pending() ?? [];
        if (pending.Count == 0)
            return CommandOutput.Text("No drafted skill is waiting for review.");
        return new CommandOutput
        {
            Title = "Drafted skills waiting for review",
            Lines = ["Without a calibrated decider every draft waits here (skill_write_safe is a gate). /skills approve <id> or /skills reject <id>."],
            Table = CommandTable.Of(["id", "skill", "replay", "why staged"], pending.Select(item => new[] { item.Id, item.Skill, item.Replay, item.Reason })),
        };
    }

    private static async Task<CommandOutput> LogAsync(SkillStore store, string? name, CancellationToken cancellationToken)
    {
        string? folder = null;
        if (name is not null)
        {
            if (!store.TryGet(name, out var skill))
                return CommandOutput.Error($"There is no skill named '{name}'.");
            folder = skill.Folder;
        }
        var commits = await store.Repository.LogAsync(folder, 20, cancellationToken).ConfigureAwait(false);
        return commits.IsEmpty ? CommandOutput.Text("No commits yet.") : new CommandOutput
        {
            Title = name is null ? "Skills history" : $"History of {name}",
            Table = CommandTable.Of(["revision", "author", "date", "change"], commits.Select(commit => new[]
            {
                Short(commit.Revision), commit.Author, commit.Date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), commit.Subject,
            })),
        };
    }

    private static CommandOutput Imported(ImportResult result, string author)
    {
        var lines = new List<string>();
        if (!result.Imported.IsEmpty)
            lines.Add($"Imported {result.Imported.Length} skill(s) as {author} ({Short(result.Revision ?? "")}): {string.Join(", ", result.Imported)}. " +
                "They are outside content: loading one taints the session.");
        lines.AddRange(result.Refused.Select(issue => $"refused {issue.Path}: {issue.Problem}"));
        if (lines.Count == 0)
            lines.Add("No skill folder (a folder with SKILL.md) was found.");
        return new CommandOutput { Lines = [.. lines], Tone = result.Refused.IsEmpty ? OutputTone.Success : OutputTone.Warning };
    }

    private static async Task<CommandOutput> StaleAsync(SlashCommandContext context, Composition.SkillServices skills, System.Collections.Immutable.ImmutableArray<string> arguments,
        CancellationToken cancellationToken)
    {
        if (skills.Curator is not { } curator)
            return CommandOutput.Warning("The curator needs the learning services, which are not running.");
        var archive = arguments.Contains("--archive");
        var days = arguments.FirstOrDefault(argument => argument != "--archive") is { } text
            ? int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : -1
            : 90;
        if (days < 0)
            return CommandOutput.Error("Give the idle limit in days, for example /skills stale 90 --archive.");
        if (!archive)
        {
            var stale = await curator.StaleAsync(TimeSpan.FromDays(days), cancellationToken).ConfigureAwait(false);
            return CommandOutput.Text(stale.Count == 0 ? $"No agent-authored skill has been idle for {days} days."
                : $"Idle for {days} days (agent-authored, not pinned): {string.Join(", ", stale.Select(skill => skill.Name))}. Add --archive to move them to .archive/.");
        }
        var archived = await curator.ArchiveStaleAsync(TimeSpan.FromDays(days), context.Session.Id, cancellationToken).ConfigureAwait(false);
        return CommandOutput.Success(archived.Count == 0 ? "Nothing to archive." : $"Archived: {string.Join(", ", archived)}.");
    }

    private static string Short(string revision) => revision.Length > 12 ? revision[..12] : revision;
}

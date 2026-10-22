using System.Collections.Immutable;
using System.Globalization;
using Corvid.Abstractions.Events;
using Corvid.Host.Composition;
using Corvid.Mcp;

namespace Corvid.Host.Commands;

/// <summary>
/// <c>/mcp</c> (C-018, C-022, C-033): the configured servers, and review of the tool definitions held back because they are
/// new or changed since the server was pinned, flagged as poisoned, or withheld after a confident <c>injection</c> verdict.
/// <c>diff</c> shows the held definition against its pin; <c>accept</c> pins exactly that definition and offers the tool from
/// the next turn; <c>allow</c> offers a flagged or withheld one as an untrusted server's tool; <c>forget</c> drops a server's
/// pins. <c>corvid mcp diff|accept|allow|forget</c> runs the same code (<see cref="Run"/>) outside a chat.
/// </summary>
public static class McpCommands
{
    public const string Usage =
        "[list] | diff <server> <tool> | accept <server> <tool|--all> [--hash <hex>] | allow <server> <tool> [--hash <hex>] | forget <server>";

    /// <summary>The command a hint names: <c>/mcp</c> in a chat, <c>corvid mcp</c> from the CLI.</summary>
    public const string SlashPrefix = "/mcp";

    internal static ValueTask<CommandOutput> RunAsync(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        if (context.Runtime.Mcp is not { } mcp)
            return ValueTask.FromResult(CommandOutput.Text("No MCP servers are configured (Corvid:Mcp:Servers)."));
        return ValueTask.FromResult(Run(mcp, invocation.Arguments, context.Runtime.Log));
    }

    /// <summary>
    /// One <c>/mcp</c> command over <paramref name="mcp"/>. <paramref name="log"/> receives <see cref="McpPinsForgotten"/> for
    /// <c>forget</c> (held and accepted definitions go to the log <paramref name="mcp"/> was started with);
    /// <paramref name="prefix"/> is how the hints name the command.
    /// </summary>
    public static CommandOutput Run(McpClients mcp, ImmutableArray<string> arguments, IEventLog? log = null, string prefix = SlashPrefix)
    {
        ArgumentNullException.ThrowIfNull(mcp);
        ArgumentNullException.ThrowIfNull(prefix);
        return arguments switch
        {
            [] or ["list"] => List(mcp),
            ["diff", var server, var tool] => Diff(mcp, server, tool, prefix),
            ["accept", var server, "--all"] => AcceptAll(mcp, server, prefix),
            ["accept", var server, var tool] => Accepted(mcp.Accept(server, tool), hashGiven: false),
            ["accept", var server, var tool, "--hash", var hash] => Accepted(mcp.Accept(server, tool, expectedHash: hash), hashGiven: true),
            ["allow", var server, var tool] => Accepted(mcp.Accept(server, tool, allowFlagged: true), hashGiven: false),
            ["allow", var server, var tool, "--hash", var hash] => Accepted(mcp.Accept(server, tool, allowFlagged: true, expectedHash: hash), hashGiven: true),
            ["forget", var server] => Forget(mcp.PinStore, server, log, prefix),
            _ => CommandOutput.Error($"Usage: {prefix} " + Usage),
        };
    }

    private static CommandOutput List(McpClients mcp)
    {
        var status = mcp.Status();
        var lines = status.SelectMany(server => server.Issues.Select(issue => $"{server.Name}: left out {issue}")).ToList();
        lines.AddRange(status.Where(server => server.ToolsChanged).Select(server => $"{server.Name}: {McpServerStatus.ToolsChangedNote}"));
        if (status.Any(server => !server.Held.IsEmpty))
            lines.Add("Held tools are not offered until reviewed: /mcp diff <server> <tool> shows what changed, /mcp accept pins it, " +
                "/mcp allow offers a flagged or withheld one as an untrusted tool.");
        lines.Add("Untrusted servers' tools ask before every call, and their descriptions and results taint the session; trusted servers' hints are honoured.");
        return new CommandOutput
        {
            Title = "MCP servers",
            Table = CommandTable.Of(["server", "transport", "trust", "state", "tools", "held", "detail"], status.Select(server => new[]
            {
                server.Name,
                server.Transport.ToString().ToLowerInvariant() + (server.Tasks ? ", tasks" : ""),
                server.Trust.ToString().ToLowerInvariant(),
                server.State.ToString().ToLowerInvariant(),
                server.Tools.IsEmpty ? "-" : string.Join(", ", server.Tools),
                server.Held.IsEmpty ? "-" : server.Held.Length.ToString(CultureInfo.InvariantCulture),
                server.Detail ?? "",
            })),
            Lines = [.. lines],
            Tone = status.Any(server => server.State == McpServerState.Failed || !server.Held.IsEmpty || server.ToolsChanged)
                ? OutputTone.Warning : OutputTone.Normal,
        };
    }

    private static McpHeldTool? Held(McpClients mcp, string server, string tool) =>
        mcp.Status().FirstOrDefault(status => status.Name == server)?.Held.FirstOrDefault(held => held.Tool == tool);

    /// <summary>
    /// The held definition against its pin, field by field: text as before and after, JSON as a line diff. A withheld tool's
    /// pin is the same definition, so it is shown whole, with the decision that withheld it. The last line is the exact
    /// command that takes it, with the hash (C-033), so a review in another process accepts what it showed.
    /// </summary>
    private static CommandOutput Diff(McpClients mcp, string server, string tool, string prefix)
    {
        if (Held(mcp, server, tool) is not { } held)
            return CommandOutput.Error($"{server} has no held tool named '{tool}'.");
        var hash = held.Definition.Hash[..McpClients.MinHashPrefix];
        var verb = McpHoldReasons.NeedsReview(held.Reason) ? "allow" : "accept";
        var next = $"{prefix} {verb} {server} {tool} --hash {hash}";
        var lines = new List<string>();
        lines.Add(held.Reason switch
        {
            McpHoldReasons.Flagged => $"Flagged: its text matches a poisoning pattern at \"{held.Flag}\". {prefix} allow offers it as an untrusted tool.",
            McpHoldReasons.Injection => $"Withheld: the injection question judged its description to instruct the agent (decision {held.Decision}; " +
                $"pinned {held.Pin?.PinnedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "never"} UTC). " +
                $"{prefix} allow offers it as an untrusted tool (asks before every call, taints the session).",
            McpHoldReasons.Changed when held.ChangedAfterWithhold =>
                $"Changed since it was withheld on {held.Pin!.PinnedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC after an injection verdict " +
                $"(decision {held.Pin.WithheldDecision}); the new definition was not judged and accept --all leaves it out. " +
                $"{prefix} accept {server} {tool} pins the definition below as a trusted server's tool.",
            McpHoldReasons.Changed => $"Changed since it was pinned on {held.Pin!.PinnedAt:yyyy-MM-dd HH:mm} UTC. {prefix} accept pins the definition below.",
            _ => $"New since the server's tools were pinned. {prefix} accept pins the definition below.",
        });
        // A withheld definition is its pin's: the whole of it is what was judged.
        var before = held.Reason == McpHoldReasons.Injection ? null : held.Pin?.Definition;
        var after = held.Definition.Canonical;
        foreach (var field in (string[])["description", "title"])
        {
            var old = before is null ? null : McpToolDefinition.Field(before, field);
            var now = McpToolDefinition.Field(after, field);
            if (before is not null && old == now)
                continue;
            lines.Add("");
            if (before is not null)
                lines.Add($"{field}, pinned: {Visible(old) ?? "(none)"}");
            lines.Add($"{field}, {(before is null ? "listed" : "now")}: {Visible(now) ?? "(none)"}");
        }
        foreach (var field in (string[])["inputSchema", "annotations"])
        {
            var old = before is null ? null : McpToolDefinition.Field(before, field);
            var now = McpToolDefinition.Field(after, field);
            if (before is not null && old == now)
                continue;
            lines.Add("");
            lines.Add(field + (before is null ? ":" : " (- pinned, + now):"));
            lines.AddRange(before is null ? Split(now).Select(line => "  " + line) : LineDiff(Split(old), Split(now)));
        }
        lines.Add("");
        lines.Add($"hash: {(before is null || held.Pin is null ? "" : held.Pin.Hash[..McpClients.MinHashPrefix] + " -> ")}{hash}");
        lines.Add("next: " + next);
        return new CommandOutput
        {
            Title = $"{server}/{tool}: held ({held.Reason})",
            Lines = [.. lines],
            Tone = McpHoldReasons.NeedsReview(held.Reason) ? OutputTone.Warning : OutputTone.Normal,
        };
    }

    private static CommandOutput Accepted(McpAcceptResult result, bool hashGiven)
    {
        if (!result.Accepted)
            return CommandOutput.Error(result.Message);
        // Without --hash nothing tied the accept to a review; the pinned hash lets the user check it against the diff they read.
        return hashGiven || result.Hash is null
            ? CommandOutput.Success(result.Message)
            : CommandOutput.Success(result.Message, $"pinned hash: {result.Hash[..McpClients.MinHashPrefix]}");
    }

    private static CommandOutput AcceptAll(McpClients mcp, string server, string prefix)
    {
        var status = mcp.Status().FirstOrDefault(item => item.Name == server);
        if (status is null)
            return CommandOutput.Error($"There is no MCP server named '{server}'.");
        var results = status.Held.Where(held => !McpHoldReasons.NeedsReview(held.Reason) && !held.ChangedAfterWithhold)
            .Select(held => mcp.Accept(server, held.Tool)).ToList();
        var review = status.Held.Where(held => McpHoldReasons.NeedsReview(held.Reason)).Select(held => held.Tool).ToList();
        // Review of #47, finding 2: a withheld definition that changed since is reviewed one by one, never taken in bulk.
        var rewithheld = status.Held.Where(held => held.ChangedAfterWithhold).Select(held => held.Tool).ToList();
        var lines = results.Select(result => result.Message).ToList();
        if (review.Count > 0)
            lines.Add($"Flagged or withheld, not accepted (review each with {prefix} diff, then {prefix} allow): {string.Join(", ", review)}.");
        if (rewithheld.Count > 0)
            lines.Add($"Changed after an injection withhold, not accepted (review each with {prefix} diff, then {prefix} accept <tool>): {string.Join(", ", rewithheld)}.");
        if (lines.Count == 0)
            return CommandOutput.Text($"{server} has no held tools.");
        return new CommandOutput
        {
            Lines = [.. lines],
            Tone = results.All(result => result.Accepted) && review.Count == 0 && rewithheld.Count == 0 ? OutputTone.Success : OutputTone.Warning,
        };
    }

    /// <summary>
    /// C-033: drops <paramref name="server"/>'s pins, so its next start is trust on first use again. Tools this process
    /// registered or holds are unchanged until it restarts. Works for a server that is no longer configured.
    /// </summary>
    public static CommandOutput Forget(IMcpPinStore pins, string server, IEventLog? log, string prefix = SlashPrefix)
    {
        ArgumentNullException.ThrowIfNull(pins);
        ArgumentException.ThrowIfNullOrEmpty(server);
        int? dropped;
        try
        {
            dropped = McpComposition.Forget(pins, server, log);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return CommandOutput.Error($"The pins cannot be changed: {exception.Message}");
        }
        if (dropped is not { } count)
            return CommandOutput.Warning($"{server} has no pinned definitions; nothing was forgotten.");
        return CommandOutput.Success(
            $"Forgot {server}'s {count} pinned definition{(count == 1 ? "" : "s")}: its next start pins whatever it lists (trust on first use).",
            $"Tools this process registered or holds from {server} are unchanged until it restarts; a running chat keeps them too ({prefix} forget changes only the pins).");
    }

    /// <summary>
    /// C-033: forgets every pinned server that is not in <paramref name="configured"/> (the raw <c>Corvid:Mcp:Servers</c>
    /// keys, so a disabled server keeps its pins). Nothing is pruned automatically.
    /// </summary>
    public static CommandOutput ForgetUnconfigured(IMcpPinStore pins, IEnumerable<string> configured, IEventLog? log, string prefix = SlashPrefix)
    {
        ArgumentNullException.ThrowIfNull(pins);
        ArgumentNullException.ThrowIfNull(configured);
        IReadOnlyList<string> stale;
        try
        {
            stale = Unconfigured(pins, configured);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return CommandOutput.Error($"The pins cannot be read: {exception.Message}");
        }
        if (stale.Count == 0)
            return CommandOutput.Text("Every pinned server is configured; nothing was forgotten.");
        var lines = new List<string>();
        foreach (var server in stale)
        {
            var output = Forget(pins, server, log, prefix);
            if (output.Tone == OutputTone.Error)
                return output;
            lines.Add(output.Lines[0]);
        }
        return CommandOutput.Success([.. lines]);
    }

    /// <summary>Pinned servers absent from <paramref name="configured"/>, in order.</summary>
    public static IReadOnlyList<string> Unconfigured(IMcpPinStore pins, IEnumerable<string> configured)
    {
        ArgumentNullException.ThrowIfNull(pins);
        ArgumentNullException.ThrowIfNull(configured);
        var names = configured.ToHashSet(StringComparer.Ordinal);
        return [.. pins.Servers().Where(server => !names.Contains(server))];
    }

    /// <summary>Hidden characters made visible, so a change that adds one shows in the diff.</summary>
    private static string? Visible(string? text)
    {
        if (text is null)
            return null;
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsControl(c) && c is not ('\n' or '\t') || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
                builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
            else
                builder.Append(c);
        }
        return builder.ToString();
    }

    private static string[] Split(string? json) => json is null ? ["(none)"] : json.Split('\n');

    /// <summary>A plain longest-common-subsequence line diff: the definitions are small (schemas are capped at 16 KiB).</summary>
    internal static ImmutableArray<string> LineDiff(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var lengths = new int[before.Count + 1, after.Count + 1];
        for (var i = before.Count - 1; i >= 0; i--)
            for (var j = after.Count - 1; j >= 0; j--)
                lengths[i, j] = before[i] == after[j] ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
        var lines = ImmutableArray.CreateBuilder<string>();
        int x = 0, y = 0;
        while (x < before.Count || y < after.Count)
        {
            if (x < before.Count && y < after.Count && before[x] == after[y])
            {
                lines.Add("  " + before[x]);
                x++;
                y++;
            }
            else if (x < before.Count && (y == after.Count || lengths[x + 1, y] >= lengths[x, y + 1]))
            {
                lines.Add("- " + before[x++]);
            }
            else
            {
                lines.Add("+ " + after[y++]);
            }
        }
        return lines.ToImmutable();
    }
}

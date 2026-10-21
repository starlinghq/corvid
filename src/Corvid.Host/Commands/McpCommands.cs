using System.Collections.Immutable;
using Corvid.Mcp;

namespace Corvid.Host.Commands;

/// <summary>
/// <c>/mcp</c> (C-018, C-022): the configured servers, and review of the tool definitions held back because they are new or
/// changed since the server was pinned, or flagged as poisoned. <c>diff</c> shows the held definition against its pin;
/// <c>accept</c> pins exactly that definition and offers the tool from the next turn; <c>allow</c> offers a flagged one as
/// an untrusted server's tool.
/// </summary>
public static class McpCommands
{
    public const string Usage = "[list] | diff <server> <tool> | accept <server> <tool|--all> | allow <server> <tool>";

    internal static ValueTask<CommandOutput> RunAsync(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        if (context.Runtime.Mcp is not { } mcp)
            return ValueTask.FromResult(CommandOutput.Text("No MCP servers are configured (Corvid:Mcp:Servers)."));
        return ValueTask.FromResult(Run(mcp, invocation.Arguments));
    }

    internal static CommandOutput Run(McpClients mcp, ImmutableArray<string> arguments) => arguments switch
    {
        [] or ["list"] => List(mcp),
        ["diff", var server, var tool] => Diff(mcp, server, tool),
        ["accept", var server, "--all"] => AcceptAll(mcp, server),
        ["accept", var server, var tool] => Accepted(mcp.Accept(server, tool)),
        ["allow", var server, var tool] => Accepted(mcp.Accept(server, tool, allowFlagged: true)),
        _ => CommandOutput.Error("Usage: /mcp " + Usage),
    };

    private static CommandOutput List(McpClients mcp)
    {
        var status = mcp.Status();
        var lines = status.SelectMany(server => server.Issues.Select(issue => $"{server.Name}: left out {issue}")).ToList();
        if (status.Any(server => !server.Held.IsEmpty))
            lines.Add("Held tools are not offered until reviewed: /mcp diff <server> <tool> shows what changed, /mcp accept pins it, " +
                "/mcp allow offers a flagged one as an untrusted tool.");
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
                server.Held.IsEmpty ? "-" : server.Held.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                server.Detail ?? "",
            })),
            Lines = [.. lines],
            Tone = status.Any(server => server.State == McpServerState.Failed || !server.Held.IsEmpty) ? OutputTone.Warning : OutputTone.Normal,
        };
    }

    private static McpHeldTool? Held(McpClients mcp, string server, string tool) =>
        mcp.Status().FirstOrDefault(status => status.Name == server)?.Held.FirstOrDefault(held => held.Tool == tool);

    /// <summary>The held definition against its pin, field by field: text as before and after, JSON as a line diff.</summary>
    private static CommandOutput Diff(McpClients mcp, string server, string tool)
    {
        if (Held(mcp, server, tool) is not { } held)
            return CommandOutput.Error($"{server} has no held tool named '{tool}'.");
        var lines = new List<string>();
        lines.Add(held.Reason switch
        {
            McpHoldReasons.Flagged => $"Flagged: its text matches a poisoning pattern at \"{held.Flag}\". /mcp allow {server} {tool} offers it as an untrusted tool.",
            McpHoldReasons.Changed => $"Changed since it was pinned on {held.Pin!.PinnedAt:yyyy-MM-dd HH:mm} UTC. /mcp accept {server} {tool} pins the definition below.",
            _ => $"New since the server's tools were pinned. /mcp accept {server} {tool} pins the definition below.",
        });
        var before = held.Pin?.Definition;
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
        lines.Add($"hash: {(held.Pin is null ? "" : held.Pin.Hash[..12] + " -> ")}{held.Definition.Hash[..12]}");
        return new CommandOutput
        {
            Title = $"{server}/{tool}: held ({held.Reason})",
            Lines = [.. lines],
            Tone = held.Reason == McpHoldReasons.Flagged ? OutputTone.Warning : OutputTone.Normal,
        };
    }

    private static CommandOutput Accepted(McpAcceptResult result) =>
        result.Accepted ? CommandOutput.Success(result.Message) : CommandOutput.Error(result.Message);

    private static CommandOutput AcceptAll(McpClients mcp, string server)
    {
        var status = mcp.Status().FirstOrDefault(item => item.Name == server);
        if (status is null)
            return CommandOutput.Error($"There is no MCP server named '{server}'.");
        var results = status.Held.Where(held => held.Reason != McpHoldReasons.Flagged).Select(held => mcp.Accept(server, held.Tool)).ToList();
        var flagged = status.Held.Where(held => held.Reason == McpHoldReasons.Flagged).Select(held => held.Tool).ToList();
        var lines = results.Select(result => result.Message).ToList();
        if (flagged.Count > 0)
            lines.Add($"Flagged, not accepted (review each with /mcp diff, then /mcp allow): {string.Join(", ", flagged)}.");
        if (lines.Count == 0)
            return CommandOutput.Text($"{server} has no held tools.");
        return new CommandOutput
        {
            Lines = [.. lines],
            Tone = results.All(result => result.Accepted) && flagged.Count == 0 ? OutputTone.Success : OutputTone.Warning,
        };
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

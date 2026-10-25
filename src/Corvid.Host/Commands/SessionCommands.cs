using System.Globalization;
using Corvid.Abstractions.Events;
using Corvid.Gateway;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;

namespace Corvid.Host.Commands;

/// <summary>
/// Sessions as lists see them (G-6, G-7): <c>/sessions</c> lists recent conversations with their titles and pins,
/// <c>/rename</c> titles the current one, <c>/recap</c> has the local generator recap it, and <c>/pin</c> keeps it at the
/// top of the app's rail. Titles and pins are shared with the windowed app through the event log and <c>ui.db</c>.
/// </summary>
internal static class SessionCommands
{
    public const int Listed = 20;

    public static ValueTask<CommandOutput> Sessions(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        var runtime = context.Runtime;
        // ui.db is a convenience: unreadable (locked, read-only, from a newer Corvid), the list goes on without pins.
        string? unpinned = null;
        IReadOnlySet<string> pinned;
        try
        {
            pinned = runtime.UiState.Pinned();
        }
        catch (Exception exception) when (State.UiStateStore.IsUnavailable(exception))
        {
            pinned = new HashSet<string>();
            unpinned = "Pins are not shown: " + exception.Message;
        }
        var sessions = runtime.Log.ListSessions(cancellationToken)
            .Where(IsConversation)
            .Select(id => SessionCatalogue.Describe(runtime.Log, id, cancellationToken))
            .OfType<SessionDescription>()
            .OrderByDescending(session => pinned.Contains(session.Id))
            .ThenByDescending(session => session.LastActivity)
            .ToList();
        if (sessions.Count == 0)
            return ValueTask.FromResult(CommandOutput.Text("No conversation is in the log yet."));
        var shown = sessions.Take(Listed).ToList();
        return ValueTask.FromResult(new CommandOutput
        {
            Title = "Sessions",
            Lines =
            [
                (sessions.Count > shown.Count ? $"The {shown.Count} most recent of {sessions.Count}, pinned first. " : "Pinned first. ")
                    + "Resume one with corvid chat --session <id>; /rename titles this one, /pin keeps it at the top.",
                .. unpinned is null ? Array.Empty<string>() : [unpinned],
            ],
            Table = CommandTable.Of(["", "session", "title", "turns", "last active"], shown.Select(session => new[]
            {
                (session.Id == context.Session.Id ? "this" : "") + (pinned.Contains(session.Id) ? (session.Id == context.Session.Id ? ", pinned" : "pinned") : ""),
                session.Id,
                session.Title ?? "(no message yet)",
                session.Turns.ToString(CultureInfo.InvariantCulture),
                session.LastActivity.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            })),
        });
    }

    public static ValueTask<CommandOutput> Rename(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Text.Length == 0)
        {
            var current = SessionCatalogue.Describe(context.Runtime.Log, context.Session.Id, cancellationToken);
            return ValueTask.FromResult(current?.Title is { } title
                ? CommandOutput.Text($"This session is titled \"{title}\"" + (current.Titled is null ? " (its first message)." : current.Titled.Source == TitleSources.User ? " (renamed)." : " (written by the local generator)."),
                    "/rename <title> renames it.")
                : CommandOutput.Text("This session has no title yet. /rename <title> gives it one."));
        }
        var titled = context.Runtime.Rename(context.Session.Id, invocation.Text);
        return ValueTask.FromResult(CommandOutput.Success($"Renamed to \"{titled.Title}\"."));
    }

    public static async ValueTask<CommandOutput> RecapAsync(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        if (context.Runtime.Titles.Unavailable is { } why)
            return CommandOutput.Warning("No recap: " + why + ".");
        var recap = await context.Runtime.RecapAsync(context.Session.Id, RecapSources.Requested, cancellationToken).ConfigureAwait(false);
        if (recap is null)
        {
            var last = context.Runtime.Log.GetHead(context.Session.Id, cancellationToken) == 0 ? null
                : Kernel.Events.SessionProjection.Load(context.Runtime.Log, context.Session.Id, cancellationToken: cancellationToken).Recap;
            return last is null
                ? CommandOutput.Text("Nothing to recap yet.")
                : new CommandOutput { Title = "Recap (nothing new since)", Lines = [.. Lines(last.Text)] };
        }
        return new CommandOutput { Title = "Recap", Lines = [.. Lines(recap.Text)] };
    }

    public static ValueTask<CommandOutput> Pin(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        var off = invocation.Arguments is ["off"] or ["remove"];
        if (!off && invocation.Arguments.Length > 0)
            return ValueTask.FromResult(CommandOutput.Error("Usage: /pin [off]."));
        // A4V2-8: a session with nothing in the log is listed nowhere (/sessions, the app's rail), so a pin would keep nothing.
        if (!off && context.Runtime.Log.GetHead(context.Session.Id, cancellationToken) == 0)
            return ValueTask.FromResult(CommandOutput.Warning("Nothing to pin yet: this session has no messages. Send one, then /pin."));
        try
        {
            context.Runtime.UiState.SetPinned(context.Session.Id, !off, DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (State.UiStateStore.IsUnavailable(exception))
        {
            return ValueTask.FromResult(CommandOutput.Warning((off ? "Not unpinned" : "Not pinned") + ": ui.db cannot be written (" + exception.Message + ")."));
        }
        return ValueTask.FromResult(CommandOutput.Success(off
            ? "Unpinned."
            : "Pinned: the app keeps this session at the top of its rail, and /sessions lists it first. /pin off unpins it."));
    }

    /// <summary>Conversations a person had, not scheduled runs or the logs Corvid keeps for itself.</summary>
    internal static bool IsConversation(string id) =>
        id is not (McpSessions.Pins or GatewaySessions.Ledger) && !id.StartsWith("job:", StringComparison.Ordinal);

    // Model output: shown without control or format characters.
    private static IEnumerable<string> Lines(string text) =>
        SessionCatalogue.Visible(text).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

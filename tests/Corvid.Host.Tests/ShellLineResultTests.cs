using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Host.Sessions;
using Corvid.Kernel.Events;
using Corvid.Kernel.Turns;

namespace Corvid.Host.Tests;

/// <summary>
/// The CLI's report of a <c>!</c> line (security L1, A4V3-2): only the turn the line opened counts, and only a call that turn
/// issued; an earlier line's result is never reported as this one's.
/// </summary>
public sealed class ShellLineResultTests
{
    private static SessionSnapshot Snapshot(params SessionEvent[] events) =>
        SessionProjection.Build("s-1", events.Select((payload, i) => new StoredEvent("s-1", i + 1, Guid.NewGuid(), DateTimeOffset.UnixEpoch, payload)));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly SessionEvent[] FirstLine =
    [
        new UserMessage("!ls"),
        new ToolCallIssued(TurnRunner.UserShellCall, "shell", Json("""{"command":"ls"}""")),
        new ToolResult(TurnRunner.UserShellCall, Json("""{"stdout":"src\n","stderr":"","exit_code":0}""")),
    ];

    [Fact]
    public void The_lines_own_turn_reports_its_result()
    {
        var snapshot = Snapshot([.. FirstLine, new UserMessage("!ls -a"), new ToolCallIssued("user-shell-2", "shell", Json("""{"command":"ls -a"}""")),
            new ToolResult("user-shell-2", Json("""{"stdout":".git\n","stderr":"","exit_code":0}"""))]);

        Assert.Equal((".git", 0, (string?)null), ComposerLine.LastShellResult(snapshot, after: 3));
        Assert.Equal(("src", 0, (string?)null), ComposerLine.LastShellResult(snapshot));
    }

    [Fact]
    public void A_turn_that_issued_no_call_reports_nothing_rather_than_an_earlier_lines_result()
    {
        // The second line ran nothing (refused before any call); the first line's result is still the session's latest.
        var snapshot = Snapshot([.. FirstLine, new UserMessage("!ls -a"), new AssistantMessage("The shell tool is off.")]);

        Assert.Equal(((string?)null, (int?)null, (string?)null), ComposerLine.LastShellResult(snapshot, after: 3));
    }
}

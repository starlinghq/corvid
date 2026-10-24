namespace Corvid.App.Core.Palette;

/// <summary>A prompt sent earlier in the workspace: its text, the session it went to and when.</summary>
public sealed record PromptHistoryEntry(string Text, string SessionId, DateTimeOffset At);

/// <summary>
/// The workspace's prompt history (docs/plan/ui.md §4.1: <c>Up</c> at the first line and <c>Ctrl+R</c> search, per
/// workspace), newest first. The engine reads it from the event log's user messages across the workspace's sessions; the
/// demo serves the scenario's prompts (<see cref="DemoPromptHistory"/>).
/// </summary>
public interface IPromptHistory
{
    Task<IReadOnlyList<PromptHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The demo scenario's prompts in the corvid workspace, as its sessions sent them on the frozen clock (cli-77b0 works in
/// corvid-vault, so its prompt is not here).
/// </summary>
public sealed class DemoPromptHistory(TimeProvider time) : IPromptHistory
{
    public Task<IReadOnlyList<PromptHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var zone = time.LocalTimeZone;
        var day = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone);
        DateTimeOffset At(int daysBack, int hour, int minute)
        {
            var wall = new DateTime(day.Year, day.Month, day.Day, hour, minute, 0, DateTimeKind.Unspecified).AddDays(-daysBack);
            return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
        }

        IReadOnlyList<PromptHistoryEntry> entries =
        [
            new("write up spike S4: Kev's tool and done accuracy at the 600-token state against a transcript-sized one", "cli-c3d2", At(0, 14, 4)),
            new("also check the retry count", "cli-9f2e", At(0, 14, 3)),
            new("draft release notes for v0.2 from CHANGELOG.md and the merged pull requests", "cli-a410", At(0, 14, 1)),
            new("the sandbox test fails on CI only. find out why and fix it without touching the production launcher.", "cli-9f2e", At(0, 14, 1)),
            new("/compact keep the CI findings", "cli-9f2e", At(0, 10, 52)),
            new("run the sandbox tests again with --filter Bwrap", "cli-9f2e", At(1, 17, 12)),
        ];
        return Task.FromResult(entries);
    }
}

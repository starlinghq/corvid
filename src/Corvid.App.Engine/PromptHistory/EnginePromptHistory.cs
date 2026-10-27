using Corvid.App.Core.Palette;
using Corvid.Kernel.Events;
using LoggedUserMessage = Corvid.Abstractions.Events.UserMessage;
using McpSessions = Corvid.Abstractions.Events.McpSessions;
using GatewaySessions = Corvid.Gateway.GatewaySessions;

namespace Corvid.App.Engine.PromptHistory;

/// <summary>
/// The workspace's prompt history (docs/plan/ui.md §4.1) from the event log: every <c>UserMessage</c> a person sent in the
/// runtime's sessions, newest first, at most <see cref="MaxEntries"/>. The runtime serves one workspace, so every session in
/// its log is this workspace's. Left out: a job's runs (the job's prompt, not something typed), gateway chats (a chat's
/// messages, some of them other people's), and the stores' own sessions (MCP pins, the delivery ledger, the memory import). Search over what
/// sessions said, beyond the prompts, is EngineSessionSearch's (G-5, the episodic index).
/// <para>The log is SQLite, read synchronously, so the read runs on the pool, never on the UI thread.</para>
/// </summary>
public sealed class EnginePromptHistory(AppEngine engine) : IPromptHistory
{
    /// <summary>The most prompts a load returns.</summary>
    public const int MaxEntries = 500;

    private readonly AppEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public async Task<IReadOnlyList<PromptHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var entries = new List<PromptHistoryEntry>();
            foreach (var id in runtime.Log.ListSessions(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Typed(id))
                    continue;
                var head = runtime.Log.GetHead(id, cancellationToken);
                foreach (var item in SessionProjection.ReadRange(runtime.Log, id, 0, head, cancellationToken))
                    if (item.Payload is LoggedUserMessage { Text.Length: > 0 } message)
                        entries.Add(new PromptHistoryEntry(message.Text, id, item.RecordedAt));
            }
            return (IReadOnlyList<PromptHistoryEntry>)[.. entries.OrderByDescending(entry => entry.At).Take(MaxEntries)];
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A session whose user messages a person typed here or at another of their own surfaces (the CLI, an editor).</summary>
    internal static bool Typed(string id) =>
        id is not (McpSessions.Pins or GatewaySessions.Ledger or Corvid.Abstractions.Events.MemoryJobs.Session or Memory.EngineMemoryService.ImportSession)
        && !id.StartsWith("job:", StringComparison.Ordinal)
        && !id.StartsWith(GatewaySessions.Prefix, StringComparison.Ordinal);
}

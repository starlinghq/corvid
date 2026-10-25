namespace Corvid.App.Core.Models;

/// <summary>
/// The Models area's seam to the engine (docs/plan/ui.md §4.8): the slots, the catalogue, downloads, placement and the
/// cloud privacy posture, and the changes a person makes to them. Every change goes through the same validator
/// <c>corvid model switch</c> uses and is written to <c>corvid.json</c>; the answer says what was written or why not.
/// Nothing downloads unless <see cref="DownloadAsync"/> is called. The demo implements it now
/// (<see cref="DemoModelService"/>); <c>Corvid.App.Engine</c> will over <c>ModelCatalogue</c>, the configuration and the
/// doctor's GPU section. <see cref="Changed"/> is raised on the UI thread, also for download progress.
/// </summary>
public interface IModelService
{
    /// <summary>Raised on the UI thread whenever <see cref="Snapshot"/> changes.</summary>
    event EventHandler? Changed;

    /// <summary>The state as last loaded or changed.</summary>
    ModelsSnapshot Snapshot { get; }

    /// <summary>Reads the configuration, the catalogue and the doctor's GPU section again.</summary>
    Task<ModelsSnapshot> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces a slot's escalation ladder (the order a hard step goes up it), validated and written.</summary>
    Task<ConfigWrite> SetLadderAsync(string slot, IReadOnlyList<string> ladder, CancellationToken cancellationToken = default);

    /// <summary>
    /// The default for new sessions (A5): binds another model to a slot in <c>corvid.json</c>, as <c>corvid model switch</c>
    /// does, and new sessions start on it. An open session keeps its own choice; one that chose nothing follows it when its
    /// turn ends. "This session" is <see cref="SwitchSessionAsync"/>, a separate choice.
    /// </summary>
    Task<ConfigWrite> SwitchAsync(string slot, string model, CancellationToken cancellationToken = default);

    /// <summary>
    /// A5, this session: switches one slot of the window's open session (any slot, the decider included) until the person
    /// changes it; escalation and fallback still move off it and the turn's end returns to it. A running turn takes it at its
    /// next step, after its issued tool calls. Nothing is written to <c>corvid.json</c>.
    /// </summary>
    Task<ConfigWrite> SwitchSessionAsync(string slot, string model, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConfigWrite(false, "Switching one session's model comes with the engine."));

    /// <summary>A5: forgets the open session's choice for a slot, so it follows the default for new sessions again.</summary>
    Task<ConfigWrite> ClearSessionChoiceAsync(string slot, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConfigWrite(false, "Switching one session's model comes with the engine."));

    /// <summary>Pins a slot's model for this session, so escalation and a switch elsewhere leave it alone.</summary>
    Task<ConfigWrite> SetPinnedAsync(string slot, bool pinned, CancellationToken cancellationToken = default);

    /// <summary>Places a local model on a device (<c>cuda:0</c>, <c>cpu</c>).</summary>
    Task<ConfigWrite> SetPlacementAsync(string model, string device, CancellationToken cancellationToken = default);

    /// <summary>Queues local models for download, one at a time; progress arrives through <see cref="Changed"/>.</summary>
    Task DownloadAsync(IEnumerable<string> models, CancellationToken cancellationToken = default);

    /// <summary>Stops a download and removes its partial file, so the cache holds only finished models.</summary>
    Task CancelDownloadAsync(string model, CancellationToken cancellationToken = default);

    /// <summary>Asks a local server whether it answers.</summary>
    Task<ServerCheck> CheckServerAsync(string model, CancellationToken cancellationToken = default);

    /// <summary>Adds an OpenAI-compatible endpoint (Ollama, llama.cpp, vLLM) to the catalogue after checking it answers.</summary>
    Task<ConfigWrite> AddServerAsync(string name, string url, CancellationToken cancellationToken = default);
}

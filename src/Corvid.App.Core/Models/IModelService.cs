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

    /// <summary>Binds another model to a slot; it applies to new sessions, and to this one at its next step.</summary>
    Task<ConfigWrite> SwitchAsync(string slot, string model, CancellationToken cancellationToken = default);

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

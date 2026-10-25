using Corvid.App.Core.Memory;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private MemoryViewModel? _memory;
    private IMemoryService? _memoryService;

    /// <summary>
    /// The memory store as the views see it. Demo mode (the engine's comes from <see cref="Services"/> instead) serves Memory.png's
    /// moment on the frozen clock.
    /// <para>
    /// From the moment it exists, the rail's Memory badge follows what waits for review: counted when it is made, and again
    /// whenever the service says the store changed (A4V2-3: a <c>#</c> line typed in a session), before the page is first
    /// visited; once the page's view model exists it keeps the badge, and reads the store again on the same signal.
    /// </para>
    /// </summary>
    public IMemoryService MemoryService
    {
        get
        {
            if (_memoryService is not null)
                return _memoryService;
            _memoryService = Services.CreateMemoryService(this);
            _memoryService.Changed += OnMemoryChanged;
            _ = CountMemoryWaitingAsync(_memoryService);
            return _memoryService;
        }
    }

    /// <summary>
    /// The Memory area: staged writes, core blocks, facts and lessons. Created on first use; from then on it keeps the
    /// rail's Memory badge to the count of staged writes, and sets the status bar whenever the page shows.
    /// </summary>
    public MemoryViewModel Memory => _memory ??= new MemoryViewModel(MemoryService, Time, Shell, StatusBar, Services.CreateMemoryStatus(this));

    private void OnMemoryChanged(object? sender, EventArgs e)
    {
        if (_memory is { } memory)
            memory.StoreChanged();
        else if (_memoryService is { } service)
            _ = CountMemoryWaitingAsync(service);
    }

    /// <summary>What waits for review, for the badge, while the page's view model does not exist yet (it counts the same way).</summary>
    private async Task CountMemoryWaitingAsync(IMemoryService service)
    {
        try
        {
            var snapshot = await service.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            var review = snapshot.StagedFacts.Count + snapshot.BlockUpdates.Count;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_memory is null)
                    Shell.SetBadge(Area.Memory, review > 0 ? review : snapshot.PendingLessons.Count);
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // No badge yet; the page says what failed when it is opened.
        }
    }

    private void DisposeMemory()
    {
        if (_memoryService is not null)
            _memoryService.Changed -= OnMemoryChanged;
    }
}

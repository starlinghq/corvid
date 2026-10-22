namespace Corvid.Models;

/// <summary>
/// A model could not be placed where configuration asked (C-029): this build has the CPU natives only, or the CUDA
/// provider is compiled in but cannot start (no CUDA runtime, cuBLAS or cuDNN on the loader path, no device). The host
/// catches it to apply the placement's fallback policy; it never falls back on its own.
/// </summary>
public sealed class ModelPlacementUnavailableException(string model, string placement, string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    /// <summary>The model's catalogue name, for example <c>kev-0.8b</c>.</summary>
    public string Model { get; } = model;

    /// <summary>The placement that failed, for example <c>cuda:0</c>.</summary>
    public string Placement { get; } = placement;
}

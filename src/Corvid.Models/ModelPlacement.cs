using System.Globalization;

namespace Corvid.Models;

/// <summary>
/// Local device placements as <see cref="ModelProfile.Placement"/> spells them (C-029): <c>cpu</c> or <c>cuda:N</c>.
/// Configuration also accepts <c>cuda</c>, which means <c>cuda:0</c>.
/// </summary>
public static class ModelPlacement
{
    public const string Cpu = "cpu";

    /// <summary>The highest CUDA ordinal configuration accepts.</summary>
    public const int MaxCudaDevice = 15;

    /// <summary>Parses a configured device into its canonical placement; false for anything but <c>cpu</c>, <c>cuda</c> or <c>cuda:0</c>..<c>cuda:15</c>.</summary>
    /// <param name="cudaDevice">The CUDA ordinal, or null for the CPU.</param>
    public static bool TryParse(string? device, out string placement, out int? cudaDevice)
    {
        placement = Cpu;
        cudaDevice = null;
        var text = device?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;
        if (text.Equals(Cpu, StringComparison.OrdinalIgnoreCase))
            return true;
        int ordinal;
        if (text.Equals("cuda", StringComparison.OrdinalIgnoreCase))
            ordinal = 0;
        else if (!text.StartsWith("cuda:", StringComparison.OrdinalIgnoreCase)
                 || !int.TryParse(text.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out ordinal)
                 || ordinal > MaxCudaDevice)
            return false;
        cudaDevice = ordinal;
        placement = Cuda(ordinal);
        return true;
    }

    public static string Cuda(int device)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(device);
        return string.Create(CultureInfo.InvariantCulture, $"cuda:{device}");
    }

    /// <summary>
    /// Whether a placement is a GPU. C-030 reads <c>CorvidRuntime.DeciderPlacement</c> (or the decider binding's
    /// <see cref="ModelProfile.Placement"/>) through this: anything starting with <c>cuda</c> is the GPU path.
    /// </summary>
    public static bool IsCuda(string? placement) => placement?.StartsWith("cuda", StringComparison.Ordinal) == true;
}

using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Corvid.Host.Diagnostics;

/// <summary>One GPU as NVML reports it. NVML indexes are in PCI bus order, whatever <c>CUDA_VISIBLE_DEVICES</c> says.</summary>
public sealed record CudaDevice(int Index, string Name, string Uuid, ulong TotalBytes, ulong UsedBytes);

/// <summary>A CUDA runtime library and whether the loader finds it for this user.</summary>
/// <param name="Required">Whether a CUDA session cannot start without it (cuDNN is optional, <see cref="CudaProbe.Cudnn"/>).</param>
public sealed record CudaLibrary(string Name, bool Resolves, bool Required);

/// <summary>What <see cref="CudaProbe.Probe"/> found. Nothing is spawned and no model or CUDA context is created.</summary>
/// <param name="NvmlProblem">Why there is no driver information (no NVIDIA driver, or NVML failed); null when NVML answered.</param>
/// <param name="DriverVersion">The NVIDIA driver, for example <c>610.57.04</c>.</param>
/// <param name="CudaDriverVersion">The CUDA driver API version the driver supports, for example <c>13.3</c>.</param>
public sealed record CudaProbeResult(
    string? NvmlProblem,
    string? DriverVersion,
    string? CudaDriverVersion,
    ImmutableArray<CudaDevice> Devices,
    ImmutableArray<CudaLibrary> Libraries,
    string? VisibleDevices,
    string? DeviceOrder)
{
    /// <summary>Required libraries the loader does not find for this user.</summary>
    public IEnumerable<string> MissingLibraries => Libraries.Where(library => library.Required && !library.Resolves).Select(library => library.Name);

    /// <summary>
    /// More than one GPU and <c>CUDA_DEVICE_ORDER</c> is not <c>PCI_BUS_ID</c>: CUDA then numbers devices fastest first,
    /// so <c>cuda:0</c> need not be NVML's (and <c>nvidia-smi</c>'s) GPU 0.
    /// </summary>
    public bool OrderAmbiguous => Devices.Length > 1 && !string.Equals(DeviceOrder, "PCI_BUS_ID", StringComparison.Ordinal);

    /// <summary>
    /// How many devices CUDA numbers <c>cuda:0</c>.. for this process (<see cref="CudaProbe.CountVisible"/>), or null when
    /// NVML did not answer. NVML lists every GPU whatever <c>CUDA_VISIBLE_DEVICES</c> says.
    /// </summary>
    public int? VisibleDeviceCount => NvmlProblem is null ? CudaProbe.CountVisible(VisibleDevices, Devices) : null;
}

/// <summary>
/// The GPU half of <c>corvid doctor</c> (C-029): the NVIDIA driver through NVML (<c>libnvidia-ml.so.1</c>, loaded only
/// if present), each GPU's name, UUID and memory, and whether the CUDA libraries the CUDA build's ONNX Runtime needs resolve
/// for this user. Linux only; elsewhere it reports that it did not look.
/// </summary>
public static partial class CudaProbe
{
    private const string Nvml = "libnvidia-ml.so.1";

    /// <summary>
    /// What the CUDA build's natives need: libonnxruntime_providers_cuda.so lists cudart, cuBLAS, cuBLASLt and cuRAND as
    /// NEEDED, and libonnxruntime-genai-cuda.so adds cuFFT. Without one of them a CUDA session cannot start.
    /// </summary>
    public static ImmutableArray<string> RuntimeLibraries { get; } =
        ["libcudart.so.13", "libcublas.so.13", "libcublasLt.so.13", "libcurand.so.10", "libcufft.so.12"];

    /// <summary>
    /// cuDNN 9, which the CUDA provider dlopens only for the kernels that use it. Kev's graph and the GPU tests' tiny graph
    /// ran on the RTX 3090 without it (C-029), so it is reported, not required.
    /// </summary>
    public const string Cudnn = "libcudnn.so.9";

    public static CudaProbeResult Probe()
    {
        var visible = Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES");
        var order = Environment.GetEnvironmentVariable("CUDA_DEVICE_ORDER");
        if (!OperatingSystem.IsLinux())
            return new CudaProbeResult("the GPU probe runs on Linux only", null, null, [], [], visible, order);
        var libraries = RuntimeLibraries.Select(name => new CudaLibrary(name, Resolves(name), Required: true))
            .Append(new CudaLibrary(Cudnn, Resolves(Cudnn), Required: false)).ToImmutableArray();
        if (!NativeLibrary.TryLoad(Nvml, out var handle))
            return new CudaProbeResult($"{Nvml} is not installed (no NVIDIA driver)", null, null, [], libraries, visible, order);
        NativeLibrary.Free(handle);
        try
        {
            return QueryNvml(libraries, visible, order);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return new CudaProbeResult($"NVML failed: {exception.Message}", null, null, [], libraries, visible, order);
        }
    }

    /// <summary>
    /// How many of <paramref name="devices"/> (NVML's list) CUDA exposes under <c>CUDA_VISIBLE_DEVICES</c>: all when it is
    /// unset; otherwise the comma-separated entries up to the first invalid one, as CUDA reads it. An entry is valid when it
    /// is an NVML index that exists, a <c>GPU-</c> UUID prefix matching exactly one device, or a <c>MIG-</c> instance
    /// (counted, not resolved); an empty value, <c>-1</c>, an unknown index or UUID, or a repeated device ends the list.
    /// </summary>
    public static int CountVisible(string? visible, IReadOnlyList<CudaDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (visible is null)
            return devices.Count;
        var seen = new HashSet<int>();
        var count = 0;
        foreach (var raw in visible.Split(','))
        {
            var entry = raw.Trim();
            if (entry.StartsWith("MIG-", StringComparison.OrdinalIgnoreCase))
            {
                count++;
                continue;
            }
            CudaDevice[] matches = int.TryParse(entry, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                ? [.. devices.Where(device => device.Index == index)]
                : entry.StartsWith("GPU-", StringComparison.OrdinalIgnoreCase)
                    ? [.. devices.Where(device => device.Uuid.StartsWith(entry, StringComparison.OrdinalIgnoreCase))]
                    : [];
            if (matches.Length != 1 || !seen.Add(matches[0].Index))
                break;
            count++;
        }
        return count;
    }

    private static bool Resolves(string library)
    {
        if (!NativeLibrary.TryLoad(library, out var handle))
            return false;
        NativeLibrary.Free(handle);
        return true;
    }

    private static unsafe CudaProbeResult QueryNvml(ImmutableArray<CudaLibrary> libraries, string? visible, string? order)
    {
        var status = NvmlInit();
        if (status != 0)
            return new CudaProbeResult($"nvmlInit failed ({ErrorName(status)})", null, null, [], libraries, visible, order);
        try
        {
            var buffer = stackalloc byte[96];
            var driver = NvmlSystemGetDriverVersion(buffer, 96) == 0 ? Text(buffer, 96) : null;
            int cuda;
            var cudaVersion = NvmlSystemGetCudaDriverVersion(&cuda) == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{cuda / 1000}.{cuda % 1000 / 10}")
                : null;
            uint count;
            if (NvmlDeviceGetCount(&count) is var countStatus && countStatus != 0)
                return new CudaProbeResult($"nvmlDeviceGetCount failed ({ErrorName(countStatus)})", driver, cudaVersion, [], libraries, visible, order);
            var devices = ImmutableArray.CreateBuilder<CudaDevice>((int)count);
            for (uint index = 0; index < count; index++)
            {
                nint device;
                if (NvmlDeviceGetHandleByIndex(index, &device) != 0)
                    continue;
                var name = NvmlDeviceGetName(device, buffer, 96) == 0 ? Text(buffer, 96) : "unknown";
                var uuid = NvmlDeviceGetUuid(device, buffer, 96) == 0 ? Text(buffer, 96) : "unknown";
                NvmlMemory memory;
                if (NvmlDeviceGetMemoryInfo(device, &memory) != 0)
                    memory = default;
                devices.Add(new CudaDevice((int)index, name, uuid, memory.Total, memory.Used));
            }
            return new CudaProbeResult(null, driver, cudaVersion, devices.ToImmutable(), libraries, visible, order);
        }
        finally
        {
            _ = NvmlShutdown();
        }
    }

    private static unsafe string Text(byte* buffer, int length)
    {
        var span = new ReadOnlySpan<byte>(buffer, length);
        var end = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }

    private static string ErrorName(int status) => status switch
    {
        3 => "not supported",
        4 => "no permission",
        9 => "driver not loaded",
        12 => "library not found",
        18 => "GPU lost",
        _ => string.Create(CultureInfo.InvariantCulture, $"error {status}"),
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [LibraryImport(Nvml, EntryPoint = "nvmlInit_v2")]
    private static partial int NvmlInit();

    [LibraryImport(Nvml, EntryPoint = "nvmlShutdown")]
    private static partial int NvmlShutdown();

    [LibraryImport(Nvml, EntryPoint = "nvmlSystemGetDriverVersion")]
    private static unsafe partial int NvmlSystemGetDriverVersion(byte* version, uint length);

    [LibraryImport(Nvml, EntryPoint = "nvmlSystemGetCudaDriverVersion_v2")]
    private static unsafe partial int NvmlSystemGetCudaDriverVersion(int* version);

    [LibraryImport(Nvml, EntryPoint = "nvmlDeviceGetCount_v2")]
    private static unsafe partial int NvmlDeviceGetCount(uint* count);

    [LibraryImport(Nvml, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static unsafe partial int NvmlDeviceGetHandleByIndex(uint index, nint* device);

    [LibraryImport(Nvml, EntryPoint = "nvmlDeviceGetName")]
    private static unsafe partial int NvmlDeviceGetName(nint device, byte* name, uint length);

    [LibraryImport(Nvml, EntryPoint = "nvmlDeviceGetUUID")]
    private static unsafe partial int NvmlDeviceGetUuid(nint device, byte* uuid, uint length);

    [LibraryImport(Nvml, EntryPoint = "nvmlDeviceGetMemoryInfo")]
    private static unsafe partial int NvmlDeviceGetMemoryInfo(nint device, NvmlMemory* memory);
}

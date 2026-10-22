using System.Diagnostics;
using System.Globalization;
using Corvid.Abstractions.Fabric;
using Corvid.Fabric;
using Corvid.Host.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Starlings.Models.Kev;
using Starlings.Runtime.Onnx;

namespace Corvid.Models.Starling.Tests;

/// <summary>
/// GPU prerequisites for the <c>Category=Gpu</c> tests (C-029): a <c>-p:UseCuda=true</c> build (ONNX Runtime lists the CUDA
/// provider), an NVIDIA GPU that NVML sees, and the CUDA libraries resolving for this user. When one is missing the test
/// skips with the reason, or fails when <c>CORVID_GPU_TESTS=required</c> (CI's flag that a GPU is expected).
/// </summary>
internal static class Gpu
{
    public const string Variable = "CORVID_GPU_TESTS";

    public static bool Required => string.Equals(Environment.GetEnvironmentVariable(Variable), "required", StringComparison.OrdinalIgnoreCase);

    /// <summary>Skips or fails (when required) unless every GPU prerequisite holds.</summary>
    public static CudaProbeResult Require()
    {
        var probe = CudaProbe.Probe();
        if (Missing(probe) is not { } reason)
            return probe;
        if (Required)
            Assert.Fail($"{Variable}=required, but {reason}");
        Assert.Skip($"{reason} (set {Variable}=required to fail instead)");
        return probe;
    }

    private static string? Missing(CudaProbeResult probe)
    {
        if (KevDecider.HasCudaProvider(out var problem) != true)
            return problem is null
                ? "this build has the CPU natives (no CUDAExecutionProvider): build with -p:UseCuda=true"
                : $"ONNX Runtime did not load: {problem}";
        if (probe.Devices.IsEmpty)
            return $"NVML finds no NVIDIA GPU ({probe.NvmlProblem ?? "no devices"})";
        var missing = probe.MissingLibraries.ToArray();
        return missing.Length > 0 ? $"CUDA libraries do not resolve for this user: {string.Join(", ", missing)}" : null;
    }
}

public sealed class GpuTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Runs on every build: checks the hand-encoded graph itself, so a GPU failure is never a malformed model.</summary>
    [Fact]
    public void The_tiny_graph_adds_on_the_cpu()
    {
        using var session = OnnxModelSession.Create(TinyGraph.Add(), new OnnxSessionOptions { Provider = OnnxExecutionProvider.Cpu });

        Assert.Equal([11f, 22f, 33f], Run(session));
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void A_cuda_session_runs_the_tiny_graph_on_the_gpu()
    {
        Gpu.Require();

        using var session = OnnxModelSession.Create(TinyGraph.Add(),
            new OnnxSessionOptions { Provider = OnnxExecutionProvider.Cuda, CudaDeviceId = 0 });

        Assert.Contains(OnnxRuntimeInfo.CudaProviderName, session.Diagnostics.SessionProviders);
        Assert.Equal([11f, 22f, 33f], Run(session));
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void The_doctor_probe_reports_a_gpu_with_memory()
    {
        var probe = Gpu.Require();

        Assert.NotNull(probe.DriverVersion);
        Assert.Contains(probe.Devices, device => device.TotalBytes > 0 && device.Uuid.StartsWith("GPU-", StringComparison.Ordinal));
        TestContext.Current.TestOutputHelper?.WriteLine($"driver {probe.DriverVersion}, CUDA {probe.CudaDriverVersion}: " +
            string.Join("; ", probe.Devices.Select(device => $"{device.Index} {device.Name} {device.Uuid} {device.TotalBytes >> 20} MiB")));
    }

    /// <summary>
    /// Kev-0.8B on <c>cuda:0</c> against the CPU decider on the same request (Starling's CUDA evidence: within 1.33e-5 of
    /// PyTorch fp32). The latency bound is loose (1 s, not S1's 300 ms gate): the workstation running CI is shared.
    /// Needs the GPU first; then skips without the weights even when the GPU is required, because weights are not a GPU
    /// prerequisite.
    /// </summary>
    [Fact]
    [Trait("Category", "Gpu")]
    public async Task Kev_0_8b_on_cuda_matches_the_cpu_decider()
    {
        Gpu.Require();
        var files = KevFiles();
        var request = KevModelTests.Ingress("list the files in my workspace");

        DeciderAnswers cpu;
        using (var onCpu = KevDecider.Create(files))
            cpu = Assert.IsType<DeciderAnswers>(await onCpu.DecideAsync(request, Ct));

        using var kev = KevDecider.Create(files, KevDecider.OptionsFor("cuda:0"));
        Assert.Equal("cuda:0", kev.Placement);
        Assert.Equal("kev-0.8b", kev.Name);
        var cuda = Assert.IsType<DeciderAnswers>(await kev.DecideAsync(request, Ct));
        Assert.Equal(cpu.Answers.Keys.Order(), cuda.Answers.Keys.Order());
        foreach (var (key, answer) in cpu.Answers)
        {
            var other = cuda.Answers[key];
            Assert.True(other.Calibrated);
            for (var i = 0; i < answer.Probabilities.Length; i++)
                Assert.True(Math.Abs(answer.Probabilities[i] - other.Probabilities[i]) <= 1e-4,
                    $"{key}[{i}]: cpu {answer.Probabilities[i]} vs cuda {other.Probabilities[i]}");
        }

        var samples = new List<double>();
        for (var call = 0; call < 20; call++)
        {
            var started = Stopwatch.GetTimestamp();
            Assert.IsType<DeciderAnswers>(await kev.DecideAsync(request, Ct));
            samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        samples.Sort();
        var p50 = samples[(samples.Count - 1) / 2];
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"kev-0.8b on cuda:0, 4-question ingress, 20 calls: p50 {p50:0} ms, min {samples[0]:0} ms, max {samples[^1]:0} ms"));
        Assert.True(p50 < 1_000, $"p50 {p50:0} ms");
    }

    [Fact]
    public void A_cpu_build_refuses_kev_on_cuda_before_reading_the_model()
    {
        Assert.SkipWhen(KevDecider.HasCudaProvider(out _) != false, "this build has the CUDA natives");
        var files = new KevModelFiles(KevModelProfile.Kev08B, "/nonexistent/model.onnx", "/nonexistent/head.safetensors",
            "/nonexistent/head.json", "/nonexistent/tokenizer.json", null!);

        var refused = Assert.Throws<ModelPlacementUnavailableException>(() => KevDecider.Create(files, KevDecider.OptionsFor("cuda")));

        Assert.Equal("cuda:0", refused.Placement);
        Assert.Equal("kev-0.8b", refused.Model);
        Assert.Contains("-p:UseCuda=true", refused.Message, StringComparison.Ordinal);
    }

    private static KevModelFiles KevFiles()
    {
        var cache = Environment.GetEnvironmentVariable(ModelTests.CacheVariable);
        var store = string.IsNullOrEmpty(cache) ? null : new StarlingModelStore(cache);
        var status = store?.Check(StarlingModel.Kev08B, Starlings.Acquisition.CacheVerification.Size);
        Assert.SkipWhen(status?.IsReady != true,
            $"Kev-0.8B is not in ${ModelTests.CacheVariable} ({status?.Describe() ?? "unset"}); weights are not a GPU prerequisite, so this skips even when the GPU is required.");
        return store!.OpenKev(KevModel.Kev08B);
    }

    private static float[] Run(OnnxModelSession session)
    {
        using var a = OrtValue.CreateTensorValueFromMemory(new float[] { 1, 2, 3 }, [3]);
        using var b = OrtValue.CreateTensorValueFromMemory(new float[] { 10, 20, 30 }, [3]);
        using var outputs = session.Run(["a", "b"], [a, b], ["sum"], Ct);
        return outputs.First().GetTensorDataAsSpan<float>().ToArray();
    }
}

/// <summary>
/// A one-node ONNX model (<c>sum = Add(a, b)</c> over float[3], opset 13) encoded by hand in protobuf wire format: a few
/// hundred bytes built in code, so the CUDA smoke test needs no fixture file and no weights.
/// </summary>
internal static class TinyGraph
{
    public static byte[] Add()
    {
        var node = Message(w => { w.String(1, "a"); w.String(1, "b"); w.String(2, "sum"); w.String(3, "add"); w.String(4, "Add"); });
        var graph = Message(w =>
        {
            w.Bytes(1, node);
            w.String(2, "tiny");
            w.Bytes(11, Value("a"));
            w.Bytes(11, Value("b"));
            w.Bytes(12, Value("sum"));
        });
        var opset = Message(w => { w.String(1, ""); w.Varint(2, 13); });
        return Message(w =>
        {
            w.Varint(1, 8); // ir_version
            w.String(2, "corvid-tests"); // producer_name
            w.Bytes(7, graph);
            w.Bytes(8, opset);
        });
    }

    // ValueInfoProto { name, type: TypeProto { tensor_type: { elem_type: FLOAT, shape: { dim: { dim_value: 3 } } } } }
    private static byte[] Value(string name)
    {
        var dim = Message(w => w.Varint(1, 3));
        var shape = Message(w => w.Bytes(1, dim));
        var tensor = Message(w => { w.Varint(1, 1); w.Bytes(2, shape); });
        var type = Message(w => w.Bytes(1, tensor));
        return Message(w => { w.String(1, name); w.Bytes(2, type); });
    }

    private static byte[] Message(Action<Writer> build)
    {
        var writer = new Writer();
        build(writer);
        return writer.Stream.ToArray();
    }

    private sealed class Writer
    {
        public MemoryStream Stream { get; } = new();

        public void Varint(int field, ulong value)
        {
            Raw(((ulong)field << 3) | 0);
            Raw(value);
        }

        public void String(int field, string value) => Bytes(field, System.Text.Encoding.UTF8.GetBytes(value));

        public void Bytes(int field, byte[] value)
        {
            Raw(((ulong)field << 3) | 2);
            Raw((ulong)value.Length);
            Stream.Write(value);
        }

        private void Raw(ulong value)
        {
            while (value >= 0x80)
            {
                Stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            Stream.WriteByte((byte)value);
        }
    }
}

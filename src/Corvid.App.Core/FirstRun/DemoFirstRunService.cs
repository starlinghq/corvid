namespace Corvid.App.Core.FirstRun;

/// <summary>
/// The demo's doctor and setup (FirstRun.dc.html). On the fresh machine (<c>--page first-run</c>) it reports the
/// mockup's checks: no configuration yet, bubblewrap and the sandbox helper in place, no answer from Vault, and an RTX
/// 3090 with the CUDA runtime. Opened as the doctor from a configured machine, the configuration is valid and Vault
/// answers. Choices are kept in memory; nothing is written.
/// </summary>
public sealed class DemoFirstRunService(bool freshMachine) : IFirstRunService
{
    public const string Workspace = "~/src/corvid";

    /// <summary>The last choices saved, for tests and the demo's own checks.</summary>
    public SetupChoices? Saved { get; private set; }

    /// <summary>How many times the checks ran.</summary>
    public int Runs { get; private set; }

    public Task<DoctorReport> RunChecksAsync(CancellationToken cancellationToken = default)
    {
        Runs++;
        IReadOnlyList<DoctorCheck> configuration = freshMachine
            ? [
                new(CheckSections.Configuration, "configuration", CheckStatus.Ok,
                    "defaults, no `~/.config/corvid/corvid.json` yet; the app writes one when you change a setting"),
                new(CheckSections.Configuration, "data directory", CheckStatus.Ok, "writable · `~/.local/share/corvid`"),
            ]
            : [
                new(CheckSections.Configuration, "configuration", CheckStatus.Ok, "valid · `~/.config/corvid/corvid.json`"),
                new(CheckSections.Configuration, "data directory", CheckStatus.Ok, "writable · `~/.local/share/corvid`"),
            ];
        DoctorCheck vault = freshMachine
            ? new(CheckSections.Sandbox, "Vault socket", CheckStatus.Warning, "no answer at `$XDG_RUNTIME_DIR/corvid-vault.sock`",
                "Without Vault, cloud models and gateway bots are unavailable. Local models work. Fix: `systemctl --user enable --now corvid-vault`",
                "systemctl --user enable --now corvid-vault")
            : new(CheckSections.Sandbox, "Vault socket", CheckStatus.Ok, "answering at `$XDG_RUNTIME_DIR/corvid-vault.sock` · 3 secrets bound");
        IReadOnlyList<DoctorCheck> checks =
        [
            .. configuration,
            new(CheckSections.Sandbox, "bubblewrap", CheckStatus.Ok, "`bwrap` 0.11 on PATH · unprivileged user namespaces available"),
            new(CheckSections.Sandbox, "corvid-sandbox", CheckStatus.Ok, "`~/.local/lib/corvid/0.2.0/corvid-sandbox`"),
            vault,
            new(CheckSections.Gpu, "driver", CheckStatus.Ok, "NVIDIA 580.65 · CUDA driver API 13.0"),
            new(CheckSections.Gpu, "GPU 0", CheckStatus.Ok, freshMachine ? "RTX 3090 · 24 GB · nothing placed on it yet" : "RTX 3090 · 24 GB · 2 models placed on it"),
            new(CheckSections.Gpu, "onnx runtime", CheckStatus.Ok, "CUDA build: CUDAExecutionProvider is listed"),
            new(CheckSections.Gpu, "cuda runtime", CheckStatus.Ok, "libcudart.so.13, libcublas.so.13 resolve for this user"),
            new(CheckSections.Gpu, "cudnn", CheckStatus.Info, "not required by the ONNX models Corvid ships"),
        ];
        return Task.FromResult(new DoctorReport(checks, "CUDA build"));
    }

    public Task<IReadOnlyList<string>> SuggestWorkspacesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([Workspace, "~/src/starling", "~/notes"]);

    public Task<WorkspaceInfo> DescribeWorkspaceAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(new WorkspaceInfo(path, path.StartsWith("~/src/", StringComparison.Ordinal)
            ? "A git repository: checkpoints and the project's lessons are on. Secrets locations in it (`.env*`, `*.pem`) stay hidden from tools."
            : "Not a git repository: checkpoints use Corvid's own store, and there are no project lessons. Secrets locations in it (`.env*`, `*.pem`) stay hidden from tools."));

    public Task<HermesImport?> FindHermesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<HermesImport?>(new HermesImport("~/.hermes", Memories: 31, Skills: 4));

    public Task SaveAsync(SetupChoices choices, CancellationToken cancellationToken = default)
    {
        Saved = choices;
        return Task.CompletedTask;
    }
}

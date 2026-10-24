using System.Collections.Immutable;

namespace Corvid.App.Core.Safety;

/// <summary>
/// The fixed lists the Safety page shows, copied from the engine because Corvid.App.Core references only
/// Corvid.Abstractions: the six hardline codes (<c>src/Corvid.Approvals/Hardline.cs</c>, <c>HardlineRules</c>) and the
/// built-in detection pack's twelve rules (<c>src/Corvid.Rules/Pack/*/*.rule.json</c>: id, version, severity, posture,
/// title). Corvid.App.Engine reads both from the engine instead (A3); until then a change there must be copied here.
/// </summary>
public static class SafetyCatalog
{
    public const string BuiltInPack = "built-in pack";

    /// <summary>What Corvid never does, in every mode, whatever a rule or a score says.</summary>
    public static ImmutableArray<HardlineCode> Hardline { get; } =
    [
        new("disk_format", "Format, partition or wipe a disk."),
        new("raw_device_write", "Write to a raw block device."),
        new("recursive_delete_outside_workspace", "Delete recursively outside the workspace."),
        new("credential_store_write", "Write to a credential store (SSH keys, cloud credentials, the keyring)."),
        new("vault_access", "Read, write or name Vault's own directory."),
        new("safety_configuration_change", "Change its own safety configuration: modes, rules, the hardline list, detections."),
    ];

    /// <summary>The built-in pack's rules in the pack's order (by folder, then id).</summary>
    public static ImmutableArray<DetectionRule> BuiltInRules { get; } =
    [
        new("approval-denied-then-executed", 1, "A denied dangerous command ran anyway", DetectionSeverity.Critical, false) { Category = "approval-abuse" },
        new("approval-denied-then-reissued", 1, "A denied dangerous command is proposed again", DetectionSeverity.High, true) { Category = "approval-abuse" },
        new("base64-into-url", 1, "Base64 output substituted into or piped to a request", DetectionSeverity.High, true) { Category = "context-exfiltration" },
        new("env-dump-piped-to-network", 1, "The environment piped to a network command", DetectionSeverity.Critical, true) { Category = "context-exfiltration" },
        new("secret-file-to-network", 2, "A credential file sent over the network", DetectionSeverity.Critical, true) { Category = "context-exfiltration" },
        new("secret-read-then-egress", 1, "A credential read and network egress in one session", DetectionSeverity.High, true) { Category = "context-exfiltration" },
        new("credential-file-read", 1, "A credential file is read", DetectionSeverity.Medium, false) { Category = "credential-access" },
        new("injection-in-tool-result", 1, "A tool result carries instruction-override or covert-action text", DetectionSeverity.Medium, false) { Category = "prompt-injection" },
        new("base64-decode-piped-to-shell", 1, "Decoded base64 piped into a shell", DetectionSeverity.High, false) { Category = "risky-command" },
        new("curl-pipe-to-shell", 1, "A download piped into a shell", DetectionSeverity.High, false) { Category = "risky-command" },
        new("fork-bomb", 1, "A fork bomb", DetectionSeverity.High, false) { Category = "risky-command" },
        new("agent-or-ci-surface-modified", 1, "CI, git hooks, agent instructions or login files are changed", DetectionSeverity.Medium, false) { Category = "sensitive-edit" },
    ];
}

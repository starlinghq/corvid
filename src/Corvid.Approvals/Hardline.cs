using System.Collections.Immutable;
using Corvid.Approvals.Shell;

namespace Corvid.Approvals;

/// <summary>Hardline rule names. They are recorded in approval reasons; renaming one changes what logs mean.</summary>
public static class HardlineRules
{
    public const string DiskFormat = "disk_format";
    public const string RawDeviceWrite = "raw_device_write";
    public const string RecursiveDeleteOutsideWorkspace = "recursive_delete_outside_workspace";
    public const string CredentialStoreWrite = "credential_store_write";
    public const string VaultAccess = "vault_access";
    public const string SafetyConfigurationChange = "safety_configuration_change";
}

/// <summary>One reason the hardline list refuses an action, or a reason it must ask whatever the mode and rules say.</summary>
/// <param name="Code">A <see cref="HardlineRules"/> name, or an <see cref="ApprovalReasons"/> code for a forced ask.</param>
public sealed record ApprovalReason(string Code, string Detail)
{
    public override string ToString() => $"{Code}: {Detail}";
}

/// <summary>Codes for reasons that force a prompt. Recorded in approval reasons.</summary>
public static class ApprovalReasons
{
    public const string Unparsed = "unparsed";
    public const string Unresolved = "unresolved";
    public const string CredentialRead = "credential_read";
    public const string VaultSweep = "may_reach_vault";
    public const string DeviceRead = "device_read";
    public const string ProcessRead = "process_read";
    public const string Elevated = "elevated";
    public const string Configuration = "configuration_change";
    public const string TaintedEgress = "tainted_network_egress";
    public const string TaintedRecipient = "tainted_new_recipient";
    public const string Signal = "signals_processes";
}

/// <summary>
/// The hardline deny list (plan §10.2 step 3): structural, model-free and not overridable by any mode, rule or score.
/// Disk formatters; writes to raw devices; recursive deletes outside the workspace; writes to credential stores; any
/// access to Vault's directories; changes to Corvid's own safety configuration. Unparsed text gets a token backstop:
/// a formatter's name or Vault's directory name anywhere in it is a denial too.
/// </summary>
internal static class Hardline
{
    private const string VaultName = "corvid-vault";

    // Sample names a credential glob might match (".e*", "*.pem", "id_*").
    private static readonly string[] CredentialSamples = [".env", ".env.local", "server.pem", "server.key", "cert.p12", "id_rsa", "id_ed25519"];
    private static readonly string[] OrdinarySamples = ["a", "notes.txt", "main.c", "README.md"];

    public static ImmutableArray<ApprovalReason> Check(ActionAnalysis analysis, ApprovalEnvironment env)
    {
        var reasons = ImmutableArray.CreateBuilder<ApprovalReason>();
        foreach (var script in analysis.OpaqueScripts)
            Backstop(script, env, reasons);

        foreach (var command in analysis.Commands)
        {
            if (Verbs.FormatsDisks(command.Verb))
                reasons.Add(new(HardlineRules.DiskFormat, $"'{command.Verb}' formats, partitions or wipes a disk"));
            foreach (var argument in command.Arguments.IsDefault ? [] : command.Arguments)
                foreach (var vault in env.Protected.Vault)
                    if (argument.Contains(vault, StringComparison.Ordinal))
                        reasons.Add(new(HardlineRules.VaultAccess, $"'{command.Verb}' names Vault's directory {vault}"));
            foreach (var target in command.Targets)
                CheckTarget(command, target, env, reasons);
        }

        return [.. reasons.Distinct()];
    }

    public static void CheckTarget(AnalyzedCommand command, ActionTarget target, ApprovalEnvironment env,
        ImmutableArray<ApprovalReason>.Builder reasons)
    {
        if (target.Kind != TargetKind.Path)
            return;
        var changes = target.Access is TargetAccess.Write or TargetAccess.Delete;
        // Moving or renaming a directory (a delete at its old path) carries everything under it, recursive or not.
        var wholeTree = target.Recursive || target.Access == TargetAccess.Delete;
        var shown = target.Written ?? target.Value;

        foreach (var vault in env.Protected.Vault)
        {
            var reach = ReachOf(target, vault);
            if (reach == Reach.Direct || reach == Reach.Ancestor && wholeTree && changes)
                reasons.Add(new(HardlineRules.VaultAccess, $"'{command.Verb}' {Verb(target)} {shown}, in Vault's directory {vault}"));
        }

        if (changes && IsDevice(target))
            reasons.Add(new(HardlineRules.RawDeviceWrite, $"'{command.Verb}' writes the device {shown}"));

        if (target.Access == TargetAccess.Delete && target.Recursive && target.Location == PathLocation.Outside)
            reasons.Add(new(HardlineRules.RecursiveDeleteOutsideWorkspace,
                $"'{command.Verb}' deletes {shown} recursively, outside the workspace {env.Workspace}"));

        if (changes && (CredentialReach(target, env) is Reach.Direct || CredentialReach(target, env) is Reach.Ancestor && wholeTree))
            reasons.Add(new(HardlineRules.CredentialStoreWrite, $"'{command.Verb}' {Verb(target)} the credential store {shown}"));

        if (changes)
            foreach (var configuration in env.Protected.SafetyConfiguration)
            {
                var reach = ReachOf(target, configuration);
                if (reach == Reach.Direct || reach == Reach.Ancestor && wholeTree)
                    reasons.Add(new(HardlineRules.SafetyConfigurationChange,
                        $"'{command.Verb}' {Verb(target)} {shown}, Corvid's safety configuration"));
            }
    }

    /// <summary>Reasons that force a prompt without denying: reading credentials or devices, sweeping over Vault.</summary>
    public static void Sensitive(AnalyzedCommand command, ActionTarget target, ApprovalEnvironment env,
        ImmutableArray<ApprovalReason>.Builder reasons)
    {
        if (target.Kind != TargetKind.Path || target.Access is TargetAccess.Write or TargetAccess.Delete)
            return;
        var shown = target.Written ?? target.Value;
        var credential = CredentialReach(target, env);
        if (credential == Reach.Direct || credential == Reach.Ancestor && target.Recursive)
            reasons.Add(new(ApprovalReasons.CredentialRead, $"'{command.Verb}' reads {shown}, which holds or contains credentials"));
        if (target.Recursive && env.Protected.Vault.Any(vault => ReachOf(target, vault) == Reach.Ancestor))
            reasons.Add(new(ApprovalReasons.VaultSweep, $"'{command.Verb}' reads all of {shown}, which contains Vault's directory"));
        if (IsDevice(target))
            reasons.Add(new(ApprovalReasons.DeviceRead, $"'{command.Verb}' reads the device {shown}"));
        // Another process's memory, environment or open files (/proc/<pid>/...) can hold secrets.
        if (target.Value.StartsWith("/proc/", StringComparison.Ordinal) && PathMath.Components(target.Value) is [_, var process, ..]
            && process != "self" && (target.Pattern || process.All(char.IsAsciiDigit)))
            reasons.Add(new(ApprovalReasons.ProcessRead, $"'{command.Verb}' reads another process's state, {shown}"));
    }

    private static string Verb(ActionTarget target) => target.Access switch
    {
        TargetAccess.Write => "writes",
        TargetAccess.Delete => "deletes",
        TargetAccess.Execute => "runs",
        _ => "reads",
    };

    private static bool IsDevice(ActionTarget target) =>
        target.Value == "/dev"
        || target.Value.StartsWith("/dev/", StringComparison.Ordinal) && !target.Value.StartsWith("/dev/shm/", StringComparison.Ordinal);

    private static Reach CredentialReach(ActionTarget target, ApprovalEnvironment env)
    {
        var reach = Reach.None;
        foreach (var store in env.Protected.CredentialStores)
            reach = Max(reach, ReachOf(target, store));
        if (reach != Reach.Direct && target.Value.Length > 0 && !target.Dynamic)
        {
            var name = target.Value[(target.Value.LastIndexOf('/') + 1)..];
            // A pattern counts only when it singles credentials out: "*.pem" or ".env*", not "*", which matches anything.
            if (target.Pattern
                    ? CredentialSamples.Any(sample => ProtectedPathMatch.Component(name, sample, dotglob: false))
                      && !OrdinarySamples.Any(sample => ProtectedPathMatch.Component(name, sample, dotglob: false))
                    : ProtectedPaths.IsCredentialFileName(name))
                reach = Reach.Direct;
        }

        return reach;
    }

    private static Reach Max(Reach left, Reach right) => (Reach)Math.Max((int)left, (int)right);

    /// <summary>How a target relates to a protected directory or file.</summary>
    private static Reach ReachOf(ActionTarget target, string protectedPath)
    {
        // A path relative to an unknown directory cannot be placed; it is a forced prompt, not a denial.
        if (!target.Value.StartsWith('/'))
            return Reach.None;
        if (target.Dynamic)
        {
            // Only the static prefix is known: inside the protected path is as good as direct; above it, it might be.
            if (target.Value.Length == 0)
                return Reach.None;
            return PathMath.IsAtOrUnder(target.Value, protectedPath) ? Reach.Direct : Reach.None;
        }

        if (!target.Pattern)
            return PathMath.IsAtOrUnder(target.Value, protectedPath) ? Reach.Direct
                : PathMath.IsUnder(protectedPath, target.Value) ? Reach.Ancestor
                : Reach.None;
        return ProtectedPathMatch.Pattern(target.Value, protectedPath);
    }

    private static void Backstop(string text, ApprovalEnvironment env, ImmutableArray<ApprovalReason>.Builder reasons)
    {
        if (text.Contains(VaultName, StringComparison.Ordinal) || env.Protected.Vault.Any(vault => text.Contains(vault, StringComparison.Ordinal)))
            reasons.Add(new(HardlineRules.VaultAccess, "unparsed text names Vault's directory"));
        var tokens = text.Split([' ', '\t', '\n', ';', '&', '|', '(', ')', '`', '$', '"', '\'', '{', '}', '<', '>', '/', '\\'],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens.Where(Verbs.FormatsDisks).Distinct())
            reasons.Add(new(HardlineRules.DiskFormat, $"unparsed text runs '{token}'"));
    }
}

internal enum Reach
{
    None = 0,
    /// <summary>The target is an ancestor of the protected path: only a recursive operation reaches it.</summary>
    Ancestor = 1,
    /// <summary>The target is the protected path or inside it.</summary>
    Direct = 2,
}

/// <summary>Glob matching against protected paths. Leading dots match <c>*</c> here, as with <c>dotglob</c>, to stay conservative.</summary>
internal static class ProtectedPathMatch
{
    public static Reach Pattern(string pattern, string protectedPath)
    {
        var parts = PathMath.Components(pattern);
        var target = PathMath.Components(protectedPath);
        var shared = Math.Min(parts.Length, target.Length);
        for (var i = 0; i < shared; i++)
            if (!Component(parts[i], target[i]))
                return Reach.None;
        return parts.Length >= target.Length ? Reach.Direct : Reach.Ancestor;
    }

    /// <summary>bash pattern matching for one path component: <c>*</c>, <c>?</c> and bracket expressions.</summary>
    /// <param name="dotglob">When false, a leading dot must be matched literally, as bash does by default.</param>
    public static bool Component(string pattern, string name, bool dotglob = true) =>
        (dotglob || !name.StartsWith('.') || pattern.StartsWith('.')) && Match(pattern, 0, name, 0);

    private static bool Match(string pattern, int p, string name, int n)
    {
        while (p < pattern.Length)
        {
            var c = pattern[p];
            switch (c)
            {
                case '*':
                    while (p < pattern.Length && pattern[p] == '*')
                        p++;
                    if (p == pattern.Length)
                        return true;
                    for (var k = n; k <= name.Length; k++)
                        if (Match(pattern, p, name, k))
                            return true;
                    return false;
                case '?':
                    if (n >= name.Length)
                        return false;
                    p++;
                    n++;
                    break;
                case '[':
                {
                    var close = pattern.IndexOf(']', p + 2);
                    if (close < 0)
                        goto default;
                    if (n >= name.Length)
                        return false;
                    var set = pattern[(p + 1)..close];
                    var negate = set.Length > 0 && set[0] is '!' or '^';
                    if (negate)
                        set = set[1..];
                    var found = false;
                    for (var i = 0; i < set.Length; i++)
                    {
                        if (i + 2 < set.Length && set[i + 1] == '-')
                        {
                            found |= name[n] >= set[i] && name[n] <= set[i + 2];
                            i += 2;
                        }
                        else
                            found |= name[n] == set[i];
                    }

                    if (found == negate)
                        return false;
                    p = close + 1;
                    n++;
                    break;
                }
                default:
                    if (n >= name.Length || name[n] != c)
                        return false;
                    p++;
                    n++;
                    break;
            }
        }

        return n == name.Length;
    }
}

using System.Runtime.InteropServices;

namespace Corvid.Sandbox.Launcher;

/// <summary>
/// Landlock, applied inside the sandbox after bubblewrap has built the mounts (a Landlocked process cannot mount, so it
/// cannot come first). It repeats the write policy in a second, independent layer: read and execute anywhere, write
/// only beneath the writable paths, plus reads and writes of existing devices under /dev. Landlock rules only grant, so
/// it cannot hide one directory inside a readable one; hiding ~/.ssh and Vault's directories stays with bubblewrap's
/// masks. From ABI 6 on it also stops the command connecting to abstract Unix sockets or signalling processes outside
/// the sandbox.
/// </summary>
internal static class Landlock
{
    private const ulong Execute = 1UL << 0;
    private const ulong WriteFile = 1UL << 1;
    private const ulong ReadFile = 1UL << 2;
    private const ulong ReadDir = 1UL << 3;
    private const ulong Abi1 = (1UL << 13) - 1; // EXECUTE through MAKE_SYM
    private const ulong Refer = 1UL << 13;      // ABI 2: rename and link across directories
    private const ulong Truncate = 1UL << 14;   // ABI 3

    private const ulong ScopeAbstractUnixSocket = 1UL << 0; // ABI 6
    private const ulong ScopeSignal = 1UL << 1;             // ABI 6

    private const nint CreateRulesetVersion = 1;
    private const nint RulePathBeneath = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RulesetAttr
    {
        public ulong HandledAccessFs;
        public ulong HandledAccessNet;
        public ulong Scoped;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PathBeneathAttr
    {
        public ulong AllowedAccess;
        public int ParentFd;
    }

    /// <summary>The kernel's Landlock ABI version, or 0 when Landlock is not built in or not enabled.</summary>
    public static int Abi()
    {
        long abi = Native.syscall(Native.SysLandlockCreateRuleset, 0, 0, CreateRulesetVersion, 0);
        return abi > 0 ? (int)abi : 0;
    }

    /// <summary>Restricts the calling thread, which then execs. Returns the ABI applied, or 0 when unavailable.</summary>
    public static unsafe int Apply(IReadOnlyList<string> writablePaths)
    {
        int abi = Abi();
        if (abi == 0)
        {
            return 0;
        }

        ulong handled = Abi1 | (abi >= 2 ? Refer : 0) | (abi >= 3 ? Truncate : 0);
        var attr = new RulesetAttr { HandledAccessFs = handled, Scoped = abi >= 6 ? ScopeAbstractUnixSocket | ScopeSignal : 0 };
        // The kernel accepts a larger struct when the fields it does not know are zero.
        int ruleset = (int)Native.syscall(Native.SysLandlockCreateRuleset, (nint)(&attr), sizeof(RulesetAttr), 0, 0);
        if (ruleset < 0)
        {
            throw new RefusedException($"Landlock: creating the ruleset failed (errno {Native.Errno}).");
        }

        try
        {
            Allow(ruleset, "/", Execute | ReadFile | ReadDir);
            Allow(ruleset, "/dev", ReadFile | ReadDir | WriteFile | (handled & Truncate));
            foreach (string path in writablePaths)
            {
                Allow(ruleset, path, handled);
            }

            if (Native.syscall(Native.SysLandlockRestrictSelf, ruleset, 0, 0, 0) != 0)
            {
                throw new RefusedException($"Landlock: restricting the process failed (errno {Native.Errno}).");
            }
        }
        finally
        {
            Native.close(ruleset);
        }

        return abi;
    }

    private static unsafe void Allow(int ruleset, string path, ulong access)
    {
        int fd = Native.open(path, Native.OPath | Native.OCloexec);
        if (fd < 0)
        {
            if (Native.Errno == Native.ENOENT)
            {
                return; // Nothing there to grant.
            }

            throw new RefusedException($"Landlock: cannot open {path} (errno {Native.Errno}).");
        }

        try
        {
            var rule = new PathBeneathAttr { AllowedAccess = access, ParentFd = fd };
            if (Native.syscall(Native.SysLandlockAddRule, ruleset, RulePathBeneath, (nint)(&rule), 0) != 0)
            {
                throw new RefusedException($"Landlock: adding the rule for {path} failed (errno {Native.Errno}).");
            }
        }
        finally
        {
            Native.close(fd);
        }
    }
}

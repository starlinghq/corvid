using System.Runtime.InteropServices;

namespace Corvid.Sandbox.Launcher;

/// <summary>
/// A seccomp deny list for the command: calls that rearrange namespaces or mounts, reach into other processes, load
/// kernel code, or widen the kernel's attack surface fail with EPERM. Everything else is allowed. An allow list would
/// break ordinary tools (git, dotnet, compilers); bubblewrap and Landlock carry the file-system policy.
/// </summary>
internal static class Seccomp
{
    // struct seccomp_data { int nr; __u32 arch; __u64 instruction_pointer; __u64 args[6]; }
    private const uint OffsetNr = 0;
    private const uint OffsetArch = 4;
    private const uint OffsetArg0Low = 16;
    private const uint OffsetArg1Low = 24;

    private const ushort LdWAbs = 0x20;   // BPF_LD | BPF_W | BPF_ABS
    private const ushort JeqK = 0x15;     // BPF_JMP | BPF_JEQ | BPF_K
    private const ushort JgeK = 0x35;     // BPF_JMP | BPF_JGE | BPF_K
    private const ushort JsetK = 0x45;    // BPF_JMP | BPF_JSET | BPF_K
    private const ushort RetK = 0x06;     // BPF_RET | BPF_K

    private const uint RetAllow = 0x7fff0000;
    private const uint RetKillProcess = 0x80000000;
    private const uint RetErrno = 0x00050000;
    private const uint EPERM = 1;

    private const uint X32SyscallBit = 0x40000000;

    // clone(2) flags that create namespaces. CLONE_NEWTIME shares its bit with the exit signal in clone(2), and is
    // reachable only through clone3 and unshare, which are refused outright.
    private const uint NamespaceFlags = 0x00020000 /* NEWNS */ | 0x02000000 /* NEWCGROUP */ | 0x04000000 /* NEWUTS */
        | 0x08000000 /* NEWIPC */ | 0x10000000 /* NEWUSER */ | 0x20000000 /* NEWPID */ | 0x40000000 /* NEWNET */;

    private const uint TIOCSTI = 0x5412;   // push bytes into a terminal's input queue: an escape through a shared tty
    private const uint TIOCLINUX = 0x541C; // the same on a virtual console

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Instruction(ushort Code, byte JumpTrue, byte JumpFalse, uint K);

    /// <summary>Syscall numbers per architecture, from the kernel's unistd tables.</summary>
    internal sealed record Table(uint AuditArch, bool HasX32, uint CloneNr, uint Clone3Nr, uint IoctlNr, IReadOnlyDictionary<string, uint> Denied);

    internal static readonly Table X64 = new(0xC000003E, true, 56, 435, 16, new Dictionary<string, uint>
    {
        ["mount"] = 165, ["umount2"] = 166, ["pivot_root"] = 155, ["chroot"] = 161, ["unshare"] = 272, ["setns"] = 308,
        ["fsopen"] = 430, ["fsconfig"] = 431, ["fsmount"] = 432, ["fspick"] = 433, ["move_mount"] = 429, ["open_tree"] = 428,
        ["mount_setattr"] = 442, ["open_by_handle_at"] = 304, ["name_to_handle_at"] = 303,
        ["ptrace"] = 101, ["process_vm_readv"] = 310, ["process_vm_writev"] = 311,
        ["kexec_load"] = 246, ["kexec_file_load"] = 320, ["init_module"] = 175, ["finit_module"] = 313, ["delete_module"] = 176,
        ["bpf"] = 321, ["perf_event_open"] = 298, ["userfaultfd"] = 323,
        ["keyctl"] = 250, ["add_key"] = 248, ["request_key"] = 249,
        ["io_uring_setup"] = 425, ["io_uring_enter"] = 426, ["io_uring_register"] = 427,
        ["swapon"] = 167, ["swapoff"] = 168, ["reboot"] = 169, ["syslog"] = 103, ["acct"] = 163, ["quotactl"] = 179,
        ["iopl"] = 172, ["ioperm"] = 173, ["uselib"] = 134, ["vhangup"] = 153,
    });

    internal static readonly Table Arm64 = new(0xC00000B7, false, 220, 435, 29, new Dictionary<string, uint>
    {
        ["mount"] = 40, ["umount2"] = 39, ["pivot_root"] = 41, ["chroot"] = 51, ["unshare"] = 97, ["setns"] = 268,
        ["fsopen"] = 430, ["fsconfig"] = 431, ["fsmount"] = 432, ["fspick"] = 433, ["move_mount"] = 429, ["open_tree"] = 428,
        ["mount_setattr"] = 442, ["open_by_handle_at"] = 265, ["name_to_handle_at"] = 264,
        ["ptrace"] = 117, ["process_vm_readv"] = 270, ["process_vm_writev"] = 271,
        ["kexec_load"] = 104, ["kexec_file_load"] = 294, ["init_module"] = 105, ["finit_module"] = 273, ["delete_module"] = 106,
        ["bpf"] = 280, ["perf_event_open"] = 241, ["userfaultfd"] = 282,
        ["keyctl"] = 219, ["add_key"] = 217, ["request_key"] = 218,
        ["io_uring_setup"] = 425, ["io_uring_enter"] = 426, ["io_uring_register"] = 427,
        ["swapon"] = 224, ["swapoff"] = 225, ["reboot"] = 142, ["syslog"] = 116, ["acct"] = 89, ["quotactl"] = 60,
        ["vhangup"] = 58,
    });

    public static Table? ForCurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => X64,
        Architecture.Arm64 => Arm64,
        _ => null,
    };

    /// <summary>The filter program. Any other architecture's calls (i386 on x86_64, for example) kill the process.</summary>
    internal static Instruction[] Build(Table table)
    {
        uint deny = RetErrno | EPERM;
        var p = new List<Instruction>
        {
            new(LdWAbs, 0, 0, OffsetArch),
            new(JeqK, 1, 0, table.AuditArch),
            new(RetK, 0, 0, RetKillProcess),
            new(LdWAbs, 0, 0, OffsetNr),
        };

        if (table.HasX32)
        {
            p.Add(new(JgeK, 0, 1, X32SyscallBit));
            p.Add(new(RetK, 0, 0, deny));
        }

        // clone: refuse namespace flags (fork and thread creation pass none).
        p.Add(new(JeqK, 0, 4, table.CloneNr));
        p.Add(new(LdWAbs, 0, 0, OffsetArg0Low));
        p.Add(new(JsetK, 0, 1, NamespaceFlags));
        p.Add(new(RetK, 0, 0, deny));
        p.Add(new(RetK, 0, 0, RetAllow));

        // ioctl: refuse terminal input injection. The request is an unsigned int, so compare the low word only.
        p.Add(new(JeqK, 0, 5, table.IoctlNr));
        p.Add(new(LdWAbs, 0, 0, OffsetArg1Low));
        p.Add(new(JeqK, 1, 0, TIOCSTI));
        p.Add(new(JeqK, 0, 1, TIOCLINUX));
        p.Add(new(RetK, 0, 0, deny));
        p.Add(new(RetK, 0, 0, RetAllow));

        // clone3 passes its flags in memory a filter cannot read. ENOSYS makes glibc fall back to clone, checked above.
        p.Add(new(JeqK, 0, 1, table.Clone3Nr));
        p.Add(new(RetK, 0, 0, RetErrno | Native.ENOSYS));

        foreach (uint nr in table.Denied.Values.Order())
        {
            p.Add(new(JeqK, 0, 1, nr));
            p.Add(new(RetK, 0, 0, deny));
        }

        p.Add(new(RetK, 0, 0, RetAllow));
        return [.. p];
    }

    /// <summary>Installs the filter on the calling thread, which then execs; seccomp filters survive execve.</summary>
    public static unsafe void Apply(Instruction[] program)
    {
        fixed (Instruction* filter = program)
        {
            // struct sock_fprog { unsigned short len; struct sock_filter *filter; }
            var fprog = new SockFprog { Length = (ushort)program.Length, Filter = filter };
            if (Native.prctl(Native.PrSetSeccomp, Native.SeccompModeFilter, (nuint)(&fprog), 0, 0) != 0)
            {
                throw new RefusedException($"seccomp: installing the filter failed (errno {Native.Errno}).");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SockFprog
    {
        public ushort Length;
        public Instruction* Filter;
    }
}

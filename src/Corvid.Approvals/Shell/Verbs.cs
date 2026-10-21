using System.Collections.Frozen;

namespace Corvid.Approvals.Shell;

/// <summary>
/// What known programs do with their arguments: which operands are paths and how they are accessed, which options write,
/// and which programs reach the network or run code. An unknown program runs code Corvid cannot see
/// (<see cref="ActionEffects.Execute"/>), so it is never treated as read-only.
/// </summary>
internal static class Verbs
{
    /// <summary>Programs that format, partition or wipe disks. The hardline list denies them outright.</summary>
    public static readonly FrozenSet<string> DiskFormatters = FrozenSet.Create(StringComparer.Ordinal,
        "mkfs", "mke2fs", "mkswap", "wipefs", "fdisk", "sfdisk", "cfdisk", "gdisk", "sgdisk", "parted", "blkdiscard",
        "mkntfs", "mkdosfs", "mkexfatfs", "mkreiserfs", "badblocks", "sg_format");

    public static bool FormatsDisks(string verb) => DiskFormatters.Contains(verb) || verb.StartsWith("mkfs.", StringComparison.Ordinal);

    // Read-only and with no path operands.
    private static readonly FrozenSet<string> Inert = FrozenSet.Create(StringComparer.Ordinal,
        "echo", "true", "false", ":", "pwd", "whoami", "id", "groups", "uname", "uptime", "nproc", "free", "ps", "pgrep",
        "pidof", "sleep", "seq", "yes", "tput", "locale", "arch", "printenv", "which", "whereis", "type", "help", "test", "[",
        "expr", "basename", "dirname", "getconf", "lscpu", "lsblk", "lsusb", "lspci", "cal", "tty", "logname", "users", "w",
        "who", "wait", "jobs", "fg", "bg", "disown", "exit", "return", "break", "continue", "umask", "ulimit", "set", "shopt",
        "times", "dirs", "compgen", "complete", "lsof", "ss", "netstat", "nvidia-smi", "sensors", "dmesg", "clear", "reset",
        "tr", "env", "df", "stty");

    // Read-only; operands are files read.
    private static readonly FrozenSet<string> Readers = FrozenSet.Create(StringComparer.Ordinal,
        "cat", "tac", "nl", "head", "tail", "less", "more", "wc", "od", "hexdump", "strings", "file", "stat", "diff", "diff3",
        "sdiff", "cmp", "comm", "fold", "fmt", "expand", "unexpand", "rev", "column", "paste", "join", "cut", "sha1sum",
        "sha224sum", "sha256sum", "sha384sum", "sha512sum", "md5sum", "b2sum", "cksum", "sum", "base64", "base32", "basenc",
        "readlink", "realpath", "ls", "dir", "vdir", "lsattr", "getfacl", "look", "tsort", "du", "tree", "sort", "uniq", "xxd",
        "zcat", "bzcat", "xzcat", "zstdcat");

    // Programs whose operands are a pattern then files (or only files with -e/-f), searched recursively when asked.
    private static readonly FrozenSet<string> Searchers = FrozenSet.Create(StringComparer.Ordinal,
        "grep", "egrep", "fgrep", "zgrep", "rg", "ag", "ack", "jq", "yq");

    // The launchers (strace, nsenter, systemd-run, script, parallel, ...) are unwrapped by the analyzer; they are listed
    // here for when they cannot be (run by a path Corvid cannot see, or a perf subcommand it does not read).
    private static readonly FrozenSet<string> Interpreters = FrozenSet.Create(StringComparer.Ordinal,
        "python", "python2", "python3", "pypy", "pypy3", "node", "nodejs", "deno", "ruby", "perl", "php", "lua", "luajit",
        "Rscript", "julia", "java", "jshell", "pwsh", "powershell", "tclsh", "expect", "osascript", "irb", "gdb", "lldb",
        "strace", "ltrace", "valgrind", "perf", "sqlite3", "psql", "mysql", "open", "xdg-open", "vim", "vi", "nvim", "nano",
        "emacs", "code", "nsenter", "unshare", "firejail", "bwrap", "systemd-run", "script", "parallel",
        "zsh", "ksh", "mksh", "ash", "yash", "posh", "fish", "csh", "tcsh");

    private static readonly FrozenSet<string> OtherShells = FrozenSet.Create(StringComparer.Ordinal,
        "zsh", "ksh", "mksh", "ash", "yash", "posh", "fish", "csh", "tcsh");

    private static readonly FrozenSet<string> BuildTools = FrozenSet.Create(StringComparer.Ordinal,
        "make", "cmake", "ninja", "meson", "gradle", "gradlew", "mvn", "ant", "bazel", "scons", "just", "task", "rake");

    private static readonly FrozenSet<string> NetworkClients = FrozenSet.Create(StringComparer.Ordinal,
        "ping", "ping6", "dig", "nslookup", "host", "whois", "traceroute", "tracepath", "mtr", "nmap", "telnet", "ftp", "lftp",
        "nc", "ncat", "netcat", "socat", "sftp", "http", "https", "xh", "aria2c");

    private static readonly FrozenSet<string> SystemChangers = FrozenSet.Create(StringComparer.Ordinal,
        "service", "reboot", "poweroff", "shutdown", "halt", "init", "telinit", "mount", "umount", "swapon", "swapoff",
        "losetup", "cryptsetup", "modprobe", "insmod", "rmmod", "iptables", "ip6tables", "nft", "ufw", "firewall-cmd",
        "useradd", "userdel", "usermod", "groupadd", "groupdel", "gpasswd", "passwd", "chpasswd", "chsh", "chfn", "visudo",
        "update-grub", "grub-install", "efibootmgr", "dracut", "mkinitcpio", "hwclock", "hdparm", "nvme", "setcap");

    // Package managers and runtimes: the listed subcommands only read; every other subcommand may download and run code.
    private static readonly FrozenDictionary<string, FrozenSet<string>> PackageManagers = new Dictionary<string, FrozenSet<string>>
    {
        ["npm"] = Set("ls", "list", "view", "info", "outdated", "why", "explain", "help", "version", "--version", "-v"),
        ["pnpm"] = Set("ls", "list", "view", "info", "outdated", "why", "help", "--version", "-v"),
        ["yarn"] = Set("list", "info", "why", "help", "--version", "-v"),
        ["bun"] = Set("pm", "--version", "-v", "help"),
        ["pip"] = Set("list", "show", "freeze", "check", "help", "--version", "-V"),
        ["pip3"] = Set("list", "show", "freeze", "check", "help", "--version", "-V"),
        ["uv"] = Set("--version", "version", "help", "tree"),
        ["pipx"] = Set("list", "--version", "help"),
        ["poetry"] = Set("show", "check", "--version", "help"),
        ["conda"] = Set("list", "info", "search", "--version"),
        ["gem"] = Set("list", "search", "info", "--version", "help"),
        ["cargo"] = Set("--version", "version", "help", "tree", "metadata", "search", "-V"),
        ["go"] = Set("version", "env", "list", "help", "doc"),
        ["dotnet"] = Set("--version", "--info", "--list-sdks", "--list-runtimes", "help", "-h", "--help"),
        ["npx"] = Set("--version"),
        ["pnpx"] = Set("--version"),
        ["bunx"] = Set("--version"),
        ["docker"] = Set("ps", "images", "version", "info", "inspect", "logs", "stats", "top", "history", "search"),
        ["podman"] = Set("ps", "images", "version", "info", "inspect", "logs", "stats", "top", "history", "search"),
        ["kubectl"] = Set("get", "describe", "logs", "version", "explain", "top", "api-resources", "cluster-info"),
        ["helm"] = Set("list", "status", "get", "show", "search", "version", "history"),
        ["terraform"] = Set("version", "show", "output", "validate", "fmt", "providers"),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, FrozenSet<string>> SystemPackageManagers = new Dictionary<string, FrozenSet<string>>
    {
        ["apt"] = Set("list", "show", "search", "policy", "depends", "rdepends", "--version"),
        ["apt-get"] = Set("--version", "check", "changelog"),
        ["apt-cache"] = Set("show", "search", "policy", "depends", "rdepends", "showpkg", "stats"),
        ["aptitude"] = Set("show", "search"),
        ["dpkg"] = Set("-l", "--list", "-L", "--listfiles", "-s", "--status", "-S", "--search", "--version"),
        ["dnf"] = Set("list", "info", "search", "repolist", "provides", "--version"),
        ["yum"] = Set("list", "info", "search", "repolist", "provides", "--version"),
        ["pacman"] = Set("-Q", "-Qi", "-Ql", "-Qs", "-Ss", "-Si", "--version"),
        ["zypper"] = Set("search", "info", "se", "if", "--version"),
        ["apk"] = Set("info", "search", "list", "--version"),
        ["snap"] = Set("list", "info", "find", "version"),
        ["flatpak"] = Set("list", "info", "search", "--version"),
        ["brew"] = Set("list", "info", "search", "--version", "config", "doctor"),
        ["nix"] = Set("search", "--version"),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenSet<string> GitReaders = FrozenSet.Create(StringComparer.Ordinal,
        "status", "log", "show", "diff", "blame", "grep", "ls-files", "ls-tree", "rev-parse", "rev-list", "describe",
        "cat-file", "shortlog", "whatchanged", "for-each-ref", "show-ref", "name-rev", "merge-base", "var", "help", "version",
        "count-objects", "check-ignore", "cherry", "reflog", "show-branch", "--version");

    private static readonly FrozenSet<string> GitNetwork = FrozenSet.Create(StringComparer.Ordinal,
        "clone", "fetch", "pull", "push", "ls-remote", "submodule", "remote");

    private static readonly FrozenSet<string> SystemctlReaders = FrozenSet.Create(StringComparer.Ordinal,
        "status", "show", "list-units", "list-unit-files", "list-timers", "list-sockets", "list-jobs", "list-dependencies",
        "is-active", "is-enabled", "is-failed", "is-system-running", "cat", "help", "--version", "get-default");

    private static FrozenSet<string> Set(params string[] items) => FrozenSet.Create(StringComparer.Ordinal, items);

    public static void Describe(CommandFacts facts, string verb, List<Field> args)
    {
        if (FormatsDisks(verb))
        {
            facts.Effects |= ActionEffects.System | ActionEffects.Write;
            foreach (var operand in Options.Scan(args, OptionTable.Empty).Operands)
                facts.Path(operand, TargetAccess.Write);
            return;
        }

        if (Inert.Contains(verb))
        {
            facts.Effects |= ActionEffects.Read;
            return;
        }

        if (Readers.Contains(verb))
        {
            Reader(facts, verb, args);
            return;
        }

        if (Searchers.Contains(verb))
        {
            Searcher(facts, verb, args);
            return;
        }

        if (Interpreters.Contains(verb))
        {
            Interpreter(facts, verb, args);
            return;
        }

        if (BuildTools.Contains(verb))
        {
            facts.Effects |= ActionEffects.Execute | ActionEffects.Write;
            facts.Subcommand = Options.Scan(args, OptionTable.Empty).Operands.FirstOrDefault(o => !o.Dynamic)?.Text;
            return;
        }

        if (NetworkClients.Contains(verb))
        {
            Network(facts, verb, args);
            return;
        }

        if (SystemChangers.Contains(verb))
        {
            facts.Effects |= ActionEffects.System;
            // Actions that overwrite a block device: its operands are device writes, so the hardline list sees them.
            var operands = Options.Scan(args, OptionTable.Empty).Operands;
            if (verb is "cryptsetup" && operands.Any(o => !o.Dynamic && o.Text is "luksFormat" or "erase" or "luksErase" or "reencrypt"
                    or "repair" or "convert")
                || verb is "nvme" && operands.Any(o => !o.Dynamic && o.Text is "format" or "sanitize" or "write" or "write-zeroes" or "dsm"
                    or "write-uncor" or "delete-ns" or "create-ns" or "fw-download" or "fw-commit"))
                foreach (var operand in operands.Where(o => o.Dynamic || o.Text.StartsWith('/')))
                    facts.Path(operand, TargetAccess.Write);
            return;
        }

        if (PackageManagers.TryGetValue(verb, out var readers))
        {
            Dispatcher(facts, args, readers, ActionEffects.Execute | ActionEffects.Network | ActionEffects.Write);
            return;
        }

        if (SystemPackageManagers.TryGetValue(verb, out readers))
        {
            Dispatcher(facts, args, readers, ActionEffects.System | ActionEffects.Network | ActionEffects.Execute | ActionEffects.Write);
            return;
        }

        switch (verb)
        {
            case "rm":
            {
                var (options, operands) = Options.Scan(args, new OptionTable(LongOptional: ["interactive"]));
                var recursive = options.Any(o => o.Name is "r" or "R" or "recursive");
                foreach (var operand in operands)
                    facts.Path(operand, TargetAccess.Delete, recursive);
                facts.Effects |= ActionEffects.Delete;
                return;
            }
            case "rmdir" or "unlink":
                foreach (var operand in Options.Scan(args, OptionTable.Empty).Operands)
                    facts.Path(operand, TargetAccess.Delete);
                facts.Effects |= ActionEffects.Delete;
                return;
            case "shred":
            {
                var (options, operands) = Options.Scan(args, new OptionTable(WithValue: "ns", LongWithValue: ["iterations", "size", "random-source"]));
                var remove = options.Any(o => o.Name is "u" or "remove" || o.Name.StartsWith("remove", StringComparison.Ordinal));
                foreach (var operand in operands)
                    facts.Path(operand, remove ? TargetAccess.Delete : TargetAccess.Write);
                facts.Effects |= ActionEffects.Write;
                return;
            }
            case "touch" or "mkdir" or "tee" or "truncate" or "mkfifo" or "mknod" or "sponge":
            {
                var (_, operands) = Options.Scan(args, new OptionTable(WithValue: verb switch
                {
                    "touch" => "rdt",
                    "mkdir" or "mkfifo" or "mknod" => "mZ",
                    "truncate" => "rs",
                    _ => "",
                }, LongWithValue: ["reference", "date", "mode", "size", "output-error"]));
                if (verb == "mknod")
                    facts.Effects |= ActionEffects.System;
                foreach (var operand in verb == "mknod" ? operands.Take(1) : operands)
                    facts.Path(operand, TargetAccess.Write);
                facts.Effects |= ActionEffects.Write;
                return;
            }
            case "chmod" or "chown" or "chgrp" or "chattr" or "setfacl":
            {
                var (options, operands) = Options.Scan(args, new OptionTable(WithValue: verb == "setfacl" ? "mMxX" : "",
                    LongWithValue: ["reference", "from", "modify", "remove", "set"]));
                var recursive = options.Any(o => o.Name is "R" or "recursive");
                var explicitMode = options.Any(o => o.Name is "reference" or "m" or "M" or "x" or "X" or "modify" or "remove" or "set")
                    || verb == "chmod" && options.Any(o => o.Name.Length == 1 && "rwxXst".Contains(o.Name[0]));
                foreach (var operand in explicitMode ? operands : operands.Skip(1))
                    facts.Path(operand, TargetAccess.Write, recursive);
                facts.Effects |= ActionEffects.Write;
                return;
            }
            case "cp" or "mv" or "install" or "ln":
                Copy(facts, verb, args);
                return;
            case "dd":
                foreach (var arg in args)
                {
                    if (arg.Text.StartsWith("if=", StringComparison.Ordinal))
                        facts.Path(arg with { Text = arg.Text[3..], Display = arg.Display[Math.Min(3, arg.Display.Length)..] }, TargetAccess.Read);
                    else if (arg.Text.StartsWith("of=", StringComparison.Ordinal))
                        facts.Path(arg with { Text = arg.Text[3..], Display = arg.Display[Math.Min(3, arg.Display.Length)..] }, TargetAccess.Write);
                    else if (arg.Dynamic)
                        facts.Unresolved($"dd operand '{arg.Display}' is computed at run time");
                }

                facts.Effects |= ActionEffects.Read;
                return;
            case "sed":
                Sed(facts, args);
                return;
            case "awk" or "gawk" or "mawk" or "nawk":
                Awk(facts, args);
                return;
            case "patch":
            {
                var (options, operands) = Options.Scan(args, new OptionTable(WithValue: "iopdDrBzVYFg", LongWithValue: ["input", "output", "directory", "strip"]));
                var directory = options.LastOrDefault(o => o.Name is "d" or "directory").Value;
                foreach (var (name, value) in options)
                    if (value is not null && name is "i" or "input")
                        facts.Path(value, TargetAccess.Read);
                    else if (value is not null && name is "o" or "output")
                        facts.Path(value, TargetAccess.Write);
                foreach (var operand in operands)
                    facts.Path(operand, TargetAccess.Write);
                // Without an explicit file, the patch names the files it changes.
                facts.Path(Under(directory, "*"), TargetAccess.Write, recursive: true);
                return;
            }
            case "tar":
                Tar(facts, args);
                return;
            case "unzip":
            {
                var (options, operands) = Options.Scan(args, new OptionTable(WithValue: "dxP"));
                if (operands.Count > 0)
                    facts.Path(operands[0], TargetAccess.Read);
                if (options.Any(o => o.Name is "l" or "t" or "v" or "Z" or "p" or "z"))
                    return;
                facts.Path(Under(options.LastOrDefault(o => o.Name == "d").Value, "*"), TargetAccess.Write, recursive: true);
                return;
            }
            case "zip":
            {
                var (options, operands) = Options.Scan(args, new OptionTable(WithValue: "bntxi", LongWithValue: ["unzip-command"]));
                if (options.Any(o => o.Name is "T" or "TT" or "unzip-command"))
                    facts.Effects |= ActionEffects.Execute;
                var recursive = options.Any(o => o.Name is "r" or "R" or "recurse-paths");
                for (var i = 0; i < operands.Count; i++)
                    facts.Path(operands[i], i == 0 ? TargetAccess.Write : TargetAccess.Read, i > 0 && recursive);
                if (options.Any(o => o.Name is "m" or "move"))
                    foreach (var operand in operands.Skip(1))
                        facts.Path(operand, TargetAccess.Delete, recursive);
                return;
            }
            case "gzip" or "gunzip" or "bzip2" or "bunzip2" or "xz" or "unxz" or "zstd" or "unzstd" or "lz4" or "compress" or "uncompress":
            {
                var (options, operands) = Options.Scan(args, new OptionTable(WithValue: "S", LongWithValue: ["suffix"]));
                var readOnly = options.Any(o => o.Name is "c" or "stdout" or "to-stdout" or "l" or "list" or "t" or "test");
                var recursive = options.Any(o => o.Name is "r" or "recursive");
                foreach (var operand in operands)
                    facts.Path(operand, readOnly ? TargetAccess.Read : TargetAccess.Write, recursive);
                if (!readOnly && !options.Any(o => o.Name is "k" or "keep"))
                    facts.Effects |= ActionEffects.Delete;
                return;
            }
            case "split" or "csplit":
            {
                var (_, operands) = Options.Scan(args, new OptionTable(WithValue: "abClnfz", LongWithValue: ["prefix", "suffix-length", "bytes", "lines"]));
                if (operands.Count > 0)
                    facts.Path(operands[0], TargetAccess.Read);
                facts.Path(Under(null, "*"), TargetAccess.Write);
                return;
            }
            case "curl":
                Curl(facts, args);
                return;
            case "wget":
                Wget(facts, args);
                return;
            case "ssh":
                Ssh(facts, args);
                return;
            case "scp" or "rsync":
                RemoteCopy(facts, verb, args);
                return;
            case "git":
                Git(facts, args);
                return;
            case "kill":
                Kill(facts, args);
                return;
            case "pkill" or "killall" or "skill" or "slay":
            {
                var (_, operands) = Options.Scan(args, new OptionTable(WithValue: "sgGPtuUnoF", LongWithValue: ["signal", "uid", "euid", "group", "parent", "session", "terminal", "pidfile", "ns", "nslist", "older-than", "younger-than", "user"]));
                foreach (var operand in operands)
                    facts.ProcessName(operand);
                if (operands.Count == 0)
                    facts.Effects |= ActionEffects.Signal;
                return;
            }
            case "renice":
            {
                facts.Effects |= ActionEffects.Signal;
                foreach (var operand in args.Where(a => a.Dynamic || a.Text.All(char.IsAsciiDigit)).Skip(1))
                    facts.Process(operand);
                return;
            }
            case "systemctl":
                Dispatcher(facts, args, SystemctlReaders, ActionEffects.System);
                return;
            case "journalctl":
                facts.Effects |= args.Any(a => a.Text.StartsWith("--vacuum", StringComparison.Ordinal) || a.Text is "--rotate" or "--flush")
                    ? ActionEffects.System : ActionEffects.Read;
                return;
            case "sysctl":
                facts.Effects |= args.Any(a => a.Dynamic || a.Text is "-w" or "--write" or "-p" or "--load" || a.Text.Contains('='))
                    ? ActionEffects.System : ActionEffects.Read;
                return;
            case "ip":
                facts.Effects |= args.Any(a => a.Dynamic || a.Text is "add" or "del" or "delete" or "set" or "flush" or "change" or "replace" or "append")
                    ? ActionEffects.System : ActionEffects.Read;
                return;
            case "date":
                facts.Effects |= args.Any(a => a.Text is "-s" || a.Text.StartsWith("--set", StringComparison.Ordinal))
                    ? ActionEffects.System : ActionEffects.Read;
                return;
            case "hostname":
                facts.Effects |= Options.Scan(args, OptionTable.Empty).Operands.Count > 0 ? ActionEffects.System : ActionEffects.Read;
                return;
            case "timedatectl" or "hostnamectl" or "localectl":
                facts.Effects |= args.Any(a => a.Dynamic || a.Text.StartsWith("set-", StringComparison.Ordinal)) ? ActionEffects.System : ActionEffects.Read;
                return;
            case "crontab":
                facts.Effects |= args.Any(a => !a.Dynamic && a.Text == "-l") ? ActionEffects.Read : ActionEffects.Schedule | ActionEffects.Write;
                return;
            case "at" or "batch" or "atrm":
                facts.Effects |= args.Any(a => !a.Dynamic && a.Text == "-l") ? ActionEffects.Read : ActionEffects.Schedule;
                return;
        }

        // Anything else runs code Corvid has no description of.
        facts.Effects |= ActionEffects.Execute;
    }

    private static Field Under(Field? directory, string name) =>
        directory is null ? new Field(name, false, true, name)
        : directory.Dynamic ? Field.Unknown(directory.Display + "/" + name)
        : new Field(directory.Text.TrimEnd('/') + "/" + name, false, true, directory.Display + "/" + name);

    private static void Reader(CommandFacts facts, string verb, List<Field> args)
    {
        var (options, operands) = Options.Scan(args, new OptionTable(
            WithValue: verb switch
            {
                "head" or "tail" => "nc",
                "cut" => "dfbc",
                "sort" => "ktoTS",
                "od" => "AjNtw",
                "xxd" => "cglos",
                "ls" or "dir" or "vdir" => "ITw",
                "tree" => "LPIo",
                "diff" => "CUIxXLW",
                "sdiff" => "IoWw",
                "less" or "more" => "oObhjkpPtTxyz",
                "du" => "BdtX",
                "column" => "cstoNOR",
                "fold" or "fmt" => "w",
                "paste" => "d",
                "join" => "jot12e",
                _ => "",
            },
            LongWithValue: ["output", "lines", "bytes", "delimiter", "fields", "key", "field-separator", "max-depth", "exclude",
                "ignore", "hide", "width", "tabsize", "files0-from", "label", "compress-program", "log-file", "LOG-FILE"]));
        var recursive = verb is "du" or "tree"
            || verb is "ls" or "dir" or "vdir" && options.Any(o => o.Name is "R" or "recursive")
            || verb is "diff" && options.Any(o => o.Name is "r" or "recursive");
        var writes = new List<Field>();
        foreach (var (name, value) in options)
        {
            if (value is null)
                continue;
            if (verb is "sort" or "tree" or "sdiff" && name is "o" or "output"
                || verb is "less" or "more" && name is "o" or "O" or "log-file" or "LOG-FILE")
                writes.Add(value);
            else if (verb is "sort" && name is "compress-program")
                facts.Effects |= ActionEffects.Execute;
            else if (name is "files0-from")
                facts.Path(value, TargetAccess.Read);
        }

        var reads = operands;
        // "uniq in out" and "xxd in out" write their second operand.
        if (verb is "uniq" or "xxd" && operands.Count > 1)
        {
            reads = operands.Take(1).ToList();
            writes.AddRange(operands.Skip(1).Take(1));
        }

        foreach (var operand in reads)
            facts.Path(operand, TargetAccess.Read, recursive);
        if (reads.Count == 0 && recursive)
            facts.Path(Field.Literal("."), TargetAccess.Read, recursive: true);
        foreach (var write in writes)
            facts.Path(write, TargetAccess.Write);
        facts.Effects |= ActionEffects.Read;
    }

    private static void Searcher(CommandFacts facts, string verb, List<Field> args)
    {
        var (options, operands) = Options.Scan(args, new OptionTable(
            WithValue: verb is "jq" or "yq" ? "f" : "efmABCdDgtTjM",
            LongWithValue: ["regexp", "file", "max-count", "after-context", "before-context", "context", "include", "exclude",
                "exclude-dir", "glob", "type", "type-not", "threads", "max-depth", "directories", "devices", "from-file", "arg",
                "argjson", "indent", "color", "colors"]));
        var explicitPattern = options.Any(o => o.Name is "e" or "f" or "regexp" or "file" or "from-file");
        foreach (var (name, value) in options)
            if (value is not null && name is "f" or "file" or "from-file")
                facts.Path(value, TargetAccess.Read);
        // "rg --pre <cmd>" runs a program on every file it searches; "ag --pager <cmd>" pipes its output into one.
        if (verb is "rg" && options.Any(o => o.Name.StartsWith("pre", StringComparison.Ordinal) && o.Name != "pretty")
            || verb is "ag" && options.Any(o => o.Name.StartsWith("pager", StringComparison.Ordinal)))
            facts.Effects |= ActionEffects.Execute;
        // A pattern computed at run time may be such an option.
        if (verb is "rg" or "ag" && operands.Count > 0 && operands[0].Dynamic && !explicitPattern)
            facts.Unresolved($"{verb} argument '{operands[0].Display}' is computed at run time and may be an option that runs a program");
        // "yq -i" edits its files in place.
        if (verb is "yq" && options.Any(o => o.Name is "i" or "inplace" or "in-place"))
        {
            foreach (var path in operands.Skip(1))
                facts.Path(path, TargetAccess.Write);
            return;
        }
        var recursive = verb is "rg" or "ag" or "ack"
            || options.Any(o => o.Name is "r" or "R" or "recursive" or "dereference-recursive"
                || o.Name is "d" or "directories" && o.Value?.Text == "recurse");
        var paths = explicitPattern ? operands : operands.Skip(1).ToList();
        foreach (var path in paths)
            facts.Path(path, TargetAccess.Read, recursive);
        if (paths.Count == 0 && recursive)
            facts.Path(Field.Literal("."), TargetAccess.Read, recursive: true);
        facts.Effects |= ActionEffects.Read;
    }

    private static void Interpreter(CommandFacts facts, string verb, List<Field> args)
    {
        // "python3 --version" and the like only report.
        if (args.Count == 1 && !args[0].Dynamic && args[0].Text is "--version" or "-V" or "-v" or "--help" or "-h" or "version")
        {
            facts.Effects |= ActionEffects.Read;
            return;
        }

        facts.Effects |= ActionEffects.Execute;
        // Other shells' scripts are outside the parsed bash subset, so they are refused (asked about) like one.
        if (OtherShells.Contains(verb))
            facts.Unresolved($"{verb} runs a script outside the parsed bash subset");
        var inline = args.Any(a => !a.Dynamic && a.Text is "-c" or "-e" or "--eval" or "-E" or "--command" or "-command");
        var script = Options.Scan(args, OptionTable.Empty).Operands.FirstOrDefault();
        if (script is not null && !inline && !args.Any(a => !a.Dynamic && a.Text == "-m"))
            facts.Path(script, TargetAccess.Execute);
        // Inline code ("python3 -c ...", "zsh -c ...") is not parsed, but the backstop still finds Vault's directory
        // name or a disk formatter in it.
        if (inline)
            foreach (var argument in args.Where(a => !a.Dynamic && !a.IsOption))
                facts.OpaqueScript(argument.Text);
    }

    private static void Dispatcher(CommandFacts facts, List<Field> args, FrozenSet<string> readers, ActionEffects otherwise)
    {
        var first = args.FirstOrDefault(arg => arg.Dynamic || !arg.Text.StartsWith('-') || readers.Contains(arg.Text));
        facts.Subcommand = first is { Dynamic: false } ? first.Text : null;
        if (first is { Dynamic: false } && readers.Contains(first.Text) || args.Count == 0)
            facts.Effects |= ActionEffects.Read;
        else
            facts.Effects |= otherwise;
        if (first is { Dynamic: true })
            facts.Unresolved($"subcommand '{first.Display}' is computed at run time");
    }

    private static void Copy(CommandFacts facts, string verb, List<Field> args)
    {
        var (options, operands) = Options.Scan(args, new OptionTable(
            WithValue: verb switch { "install" => "mogtS", "ln" => "tS", _ => "tS" },
            LongWithValue: ["target-directory", "suffix", "mode", "owner", "group"]));
        if (verb == "install" && options.Any(o => o.Name is "d" or "directory"))
        {
            foreach (var operand in operands)
                facts.Path(operand, TargetAccess.Write);
            return;
        }

        var recursive = verb == "mv" || options.Any(o => o.Name is "r" or "R" or "a" or "recursive" or "archive");
        var target = options.LastOrDefault(o => o.Name is "t" or "target-directory").Value;
        var sources = operands;
        if (target is null && operands.Count > 1)
        {
            target = operands[^1];
            sources = operands.Take(operands.Count - 1).ToList();
        }
        else if (target is null && verb == "ln" && operands.Count == 1)
            target = Field.Literal(".");

        foreach (var source in sources)
        {
            if (verb == "mv")
                facts.Path(source, TargetAccess.Delete);
            else if (verb != "ln")
                facts.Path(source, TargetAccess.Read, recursive);
        }

        if (target is not null)
            facts.Path(target, TargetAccess.Write, recursive && verb != "ln");
        facts.Effects |= ActionEffects.Write;
    }

    private static void Sed(CommandFacts facts, List<Field> args)
    {
        var (options, operands) = Options.Scan(args, new OptionTable(WithValue: "efl", LongWithValue: ["expression", "file", "line-length"]));
        var scripts = options.Where(o => o.Name is "e" or "expression").Select(o => o.Value).ToList();
        var files = operands;
        if (scripts.Count == 0 && !options.Any(o => o.Name is "f" or "file") && operands.Count > 0)
        {
            scripts.Add(operands[0]);
            files = operands.Skip(1).ToList();
        }

        var inPlace = options.Any(o => o.Name.StartsWith('i') || o.Name.StartsWith("in-place", StringComparison.Ordinal));
        var sandbox = options.Any(o => o.Name == "sandbox");
        // GNU sed's e, w, r, R and W commands run programs or touch other files; a script with any of them is opaque.
        if (!sandbox && (options.Any(o => o.Name is "f" or "file") || scripts.Any(script => script is null || script.Dynamic || !SedScript.IsSafe(script.Text))))
            facts.Effects |= ActionEffects.Execute;
        foreach (var file in files)
            facts.Path(file, inPlace ? TargetAccess.Write : TargetAccess.Read);
        facts.Effects |= ActionEffects.Read;
    }

    private static void Awk(CommandFacts facts, List<Field> args)
    {
        var (options, operands) = Options.Scan(args, new OptionTable(WithValue: "Fvf", LongWithValue: ["field-separator", "assign", "file"]));
        var programFile = options.Any(o => o.Name is "f" or "file");
        var program = programFile ? null : operands.FirstOrDefault();
        // system(), pipes, output redirection and getline reach outside the files read.
        if (programFile || program is null || program.Dynamic
            || new[] { "system", "|", ">", "getline" }.Any(token => program.Text.Contains(token, StringComparison.Ordinal)))
            facts.Effects |= ActionEffects.Execute;
        foreach (var file in programFile ? operands : operands.Skip(1))
            if (file.Dynamic || !file.Text.Contains('='))
                facts.Path(file, TargetAccess.Read);
        facts.Effects |= ActionEffects.Read;
    }

    private static void Tar(CommandFacts facts, List<Field> args)
    {
        var expanded = new List<Field>(args);
        // Old-style "tar czf out.tgz dir": the first word is a cluster of mode letters without a dash.
        if (expanded.Count > 0 && !expanded[0].Dynamic && expanded[0].Text.Length > 0 && !expanded[0].Text.StartsWith('-')
            && expanded[0].Text.All(char.IsAsciiLetter))
            expanded[0] = expanded[0] with { Text = "-" + expanded[0].Text };
        var (options, operands) = Options.Scan(expanded, new OptionTable(WithValue: "fCbHIKLMNTVgX",
            LongWithValue: ["file", "directory", "to-command", "use-compress-program", "checkpoint-action", "info-script",
                "new-volume-script", "rsh-command", "exclude", "files-from", "exclude-from", "transform", "owner", "group", "mode"]));
        if (options.Any(o => o.Name is "I" or "F" or "to-command" or "use-compress-program" or "checkpoint-action" or "info-script"
                or "new-volume-script" or "rsh-command"))
            facts.Effects |= ActionEffects.Execute;
        var archive = options.LastOrDefault(o => o.Name is "f" or "file").Value;
        var directory = options.LastOrDefault(o => o.Name is "C" or "directory").Value;
        bool Mode(params string[] names) => options.Any(o => names.Contains(o.Name));
        if (Mode("x", "extract", "get"))
        {
            if (archive is not null)
                facts.Path(archive, TargetAccess.Read);
            facts.Path(Under(directory, "*"), TargetAccess.Write, recursive: true);
        }
        else if (Mode("c", "create", "r", "append", "u", "update", "A", "catenate", "concatenate", "delete"))
        {
            if (archive is not null)
                facts.Path(archive, TargetAccess.Write);
            foreach (var operand in operands)
                facts.Path(directory is null || operand.Dynamic || operand.Text.StartsWith('/') ? operand : Under(directory, operand.Text) with { Glob = operand.Glob },
                    TargetAccess.Read, recursive: true);
        }
        else if (archive is not null)
            facts.Path(archive, TargetAccess.Read);
        facts.Effects |= ActionEffects.Read;
    }

    private static void Network(CommandFacts facts, string verb, List<Field> args)
    {
        facts.Effects |= ActionEffects.Network;
        var (options, operands) = Options.Scan(args, new OptionTable(WithValue: "cipwWstlqTPeoO", LongWithValue: ["port", "count"]));
        if (verb is "nc" or "ncat" or "netcat" or "socat" && options.Any(o => o.Name is "e" or "c" or "exec" or "sh-exec" or "lua-exec")
            || verb == "socat" && args.Any(a => a.Text.Contains("EXEC", StringComparison.OrdinalIgnoreCase) || a.Text.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase)))
            facts.Effects |= ActionEffects.Execute;
        if (verb is "nc" or "ncat" or "netcat" or "socat" or "telnet" or "ftp" or "lftp" or "sftp")
            facts.Effects |= ActionEffects.Remote;
        foreach (var operand in operands)
        {
            if (operand.Dynamic)
                facts.Url(operand);
            else if (operand.Text.Contains("://", StringComparison.Ordinal))
                facts.Url(operand);
            else if (!operand.Text.All(char.IsAsciiDigit))
                facts.Host(HostOf(operand.Text), operand.Display);
        }
    }

    private static void Curl(CommandFacts facts, List<Field> args)
    {
        facts.Effects |= ActionEffects.Network;
        var (options, operands) = Options.Scan(args, new OptionTable(WithValue: "AbcdDeEFHKmorTuUwxXyYzC",
            LongWithValue: ["output", "upload-file", "data", "data-binary", "data-raw", "data-urlencode", "data-ascii", "form",
                "form-string", "header", "user-agent", "cookie", "cookie-jar", "dump-header", "config", "url", "request", "user",
                "proxy", "referer", "max-time", "connect-timeout", "output-dir", "write-out", "json", "range", "retry", "cert", "key",
                "cacert", "resolve", "connect-to", "time-cond", "trace", "trace-ascii", "stderr", "netrc-file"]));
        foreach (var (name, value) in options)
        {
            if (value is null)
            {
                if (name is "O" or "remote-name" or "remote-name-all" or "J" or "remote-header-name")
                    facts.Path(Under(options.LastOrDefault(o => o.Name == "output-dir").Value, "*"), TargetAccess.Write);
                continue;
            }

            switch (name)
            {
                case "o" or "output" or "c" or "cookie-jar" or "D" or "dump-header" or "trace" or "trace-ascii" or "stderr":
                    facts.Path(value, TargetAccess.Write);
                    break;
                case "T" or "upload-file" or "K" or "config" or "E" or "cert" or "key" or "cacert" or "netrc-file":
                    facts.Path(value, TargetAccess.Read);
                    facts.Effects |= name is "T" or "upload-file" ? ActionEffects.Remote : ActionEffects.None;
                    break;
                case "d" or "data" or "data-binary" or "data-raw" or "data-urlencode" or "data-ascii" or "F" or "form" or "json":
                    facts.Effects |= ActionEffects.Remote;
                    // "@file" sends a file's content; "-F name=@file" and "<file" too.
                    var at = name == "data-raw" ? -1 : value.Text.IndexOfAny(['@', '<']);
                    if (value.Dynamic && value.Text.Length == 0)
                        facts.Path(value, TargetAccess.Read);
                    else if (at >= 0)
                        facts.Path(value with { Text = value.Text[(at + 1)..].Split(';')[0], Display = value.Display }, TargetAccess.Read);
                    break;
                case "X" or "request" when value.Text is not ("GET" or "HEAD" or "OPTIONS"):
                    facts.Effects |= ActionEffects.Remote;
                    break;
                case "url":
                    facts.Url(value);
                    break;
            }
        }

        foreach (var operand in operands)
            facts.Url(operand);
    }

    private static void Wget(CommandFacts facts, List<Field> args)
    {
        facts.Effects |= ActionEffects.Network;
        var (options, operands) = Options.Scan(args, new OptionTable(WithValue: "OoaPiUeTtwQ",
            LongWithValue: ["output-document", "output-file", "append-output", "directory-prefix", "input-file", "post-file",
                "body-file", "user-agent", "header", "execute", "tries", "timeout", "post-data", "method", "body-data", "load-cookies",
                "save-cookies", "config"]));
        var downloads = true;
        foreach (var (name, value) in options)
        {
            if (value is null)
            {
                if (name is "spider")
                    downloads = false;
                continue;
            }

            switch (name)
            {
                case "O" or "output-document":
                    facts.Path(value, TargetAccess.Write);
                    downloads = false;
                    break;
                case "o" or "a" or "output-file" or "append-output" or "save-cookies":
                    facts.Path(value, TargetAccess.Write);
                    break;
                case "P" or "directory-prefix":
                    facts.Path(Under(value, "*"), TargetAccess.Write, recursive: true);
                    downloads = false;
                    break;
                case "i" or "input-file" or "load-cookies" or "config":
                    facts.Path(value, TargetAccess.Read);
                    break;
                case "post-file" or "body-file":
                    facts.Path(value, TargetAccess.Read);
                    facts.Effects |= ActionEffects.Remote;
                    break;
                case "post-data" or "body-data" or "method":
                    facts.Effects |= ActionEffects.Remote;
                    break;
                case "e" or "execute":
                    facts.Unresolved("wget -e runs a wgetrc command");
                    break;
            }
        }

        if (downloads)
            facts.Path(Under(null, "*"), TargetAccess.Write, recursive: options.Any(o => o.Name is "r" or "recursive" or "m" or "mirror"));
        foreach (var operand in operands)
            facts.Url(operand);
    }

    private static void Ssh(CommandFacts facts, List<Field> args)
    {
        facts.Effects |= ActionEffects.Network | ActionEffects.Remote;
        var (options, operands) = Options.Scan(args, new OptionTable(WithValue: "bcDEeFIiJLlmOopQRSWwB"));
        foreach (var (name, value) in options)
        {
            if (value is null)
                continue;
            if (name is "i" or "F")
                facts.Path(value, TargetAccess.Read);
            else if (name is "E" or "S")
                facts.Path(value, TargetAccess.Write);
            else if (name == "o" && (value.Dynamic || value.Text.StartsWith("ProxyCommand", StringComparison.OrdinalIgnoreCase)
                || value.Text.StartsWith("LocalCommand", StringComparison.OrdinalIgnoreCase)))
                facts.Effects |= ActionEffects.Execute;
            else if (name == "J")
                facts.Host(HostOf(value.Text), value.Display, value.Dynamic);
        }

        if (operands.Count == 0)
            return;
        var destination = operands[0];
        if (destination.Text.StartsWith("ssh://", StringComparison.Ordinal))
            facts.Url(destination);
        else
            facts.Host(HostOf(destination.Text), destination.Display, destination.Dynamic);
        if (operands.Count > 1)
            facts.Effects |= ActionEffects.Execute;
    }

    private static void RemoteCopy(CommandFacts facts, string verb, List<Field> args)
    {
        var (options, operands) = Options.Scan(args, new OptionTable(
            WithValue: verb == "scp" ? "cFiloPSJDX" : "eBfT",
            LongWithValue: ["rsh", "exclude", "include", "filter", "files-from", "password-file", "log-file", "temp-dir", "rsync-path"]));
        if (options.Any(o => o.Name is "e" or "rsh" or "rsync-path" || verb == "scp" && o.Name == "S"))
            facts.Effects |= ActionEffects.Execute;
        foreach (var (name, value) in options)
            if (value is not null && name is "i" or "F" or "files-from" or "password-file")
                facts.Path(value, TargetAccess.Read);
        var recursive = verb == "rsync" && options.Any(o => o.Name is "r" or "a" or "recursive" or "archive") || verb == "scp" && options.Any(o => o.Name == "r");
        var deletes = verb == "rsync" && options.Any(o => o.Name.StartsWith("delete", StringComparison.Ordinal) || o.Name is "remove-source-files");
        for (var i = 0; i < operands.Count; i++)
        {
            var operand = operands[i];
            var last = i == operands.Count - 1 && operands.Count > 1;
            if (!operand.Dynamic && IsRemote(operand.Text, out var host))
            {
                facts.Host(host, operand.Display);
                facts.Effects |= ActionEffects.Remote;
                continue;
            }

            if (operand.Dynamic && operand.Text.Length == 0)
                facts.Effects |= ActionEffects.Network;
            facts.Path(operand, last ? TargetAccess.Write : TargetAccess.Read, recursive);
            if (last && deletes)
                facts.Path(operand, TargetAccess.Delete, recursive: true);
        }
    }

    private static bool IsRemote(string operand, out string host)
    {
        host = "";
        if (operand.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(operand, UriKind.Absolute, out var uri) || uri.IsFile)
                return false;
            host = uri.Host;
            return true;
        }

        // "host:path" and "user@host:path"; a slash before the colon makes it a local path.
        var colon = operand.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || operand.IndexOf('/', StringComparison.Ordinal) is >= 0 and var slash && slash < colon)
            return false;
        host = HostOf(operand[..colon]);
        return true;
    }

    private static string HostOf(string destination)
    {
        var at = destination.LastIndexOf('@');
        var host = at >= 0 ? destination[(at + 1)..] : destination;
        var colon = host.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && host.IndexOf(':', colon + 1) < 0 ? host[..colon] : host;
    }

    private static void Git(CommandFacts facts, List<Field> args)
    {
        var i = 0;
        Field? directory = null;
        while (i < args.Count && (args[i].Dynamic || args[i].Text.StartsWith('-')))
        {
            var option = args[i];
            if (option.Dynamic)
            {
                facts.Unresolved($"git option '{option.Display}' is computed at run time");
                facts.Effects |= ActionEffects.Execute;
                return;
            }

            switch (option.Text)
            {
                case "-C" when i + 1 < args.Count:
                    directory = args[++i];
                    break;
                case "-c" when i + 1 < args.Count:
                    // Configuration such as core.sshCommand, alias.* or core.hooksPath runs programs.
                    facts.Effects |= ActionEffects.Execute;
                    i++;
                    break;
                case "--git-dir" or "--work-tree" or "--namespace" when i + 1 < args.Count:
                    i++;
                    break;
            }

            i++;
        }

        if (i >= args.Count)
        {
            facts.Effects |= ActionEffects.Read;
            return;
        }

        var subcommand = args[i];
        if (subcommand.Dynamic)
        {
            facts.Unresolved($"git subcommand '{subcommand.Display}' is computed at run time");
            facts.Effects |= ActionEffects.Execute;
            return;
        }

        var name = subcommand.Text;
        facts.Subcommand = name;
        var rest = args.Skip(i + 1).ToList();
        var repository = Under(directory, "*");
        bool Listing(params string[] listing) => rest.All(arg => !arg.Dynamic && (listing.Contains(arg.Text) || arg.Text.StartsWith("--format", StringComparison.Ordinal)
            || arg.Text.StartsWith("--sort", StringComparison.Ordinal) || arg.Text.StartsWith("--contains", StringComparison.Ordinal)));

        // Options that make a reading subcommand write a file or run a program: "git log --output=<file>",
        // "git grep -O<cmd>". They apply before the reader shortcut, which would otherwise run them unasked.
        for (var j = 0; j < rest.Count; j++)
        {
            var arg = rest[j];
            if (arg.Dynamic)
            {
                facts.Unresolved($"git argument '{arg.Display}' is computed at run time and may be an option that writes or runs a program");
                continue;
            }

            var text = arg.Text;
            if (text.StartsWith("--output=", StringComparison.Ordinal) || text.StartsWith("--output-directory=", StringComparison.Ordinal))
                facts.Path(arg with { Text = text[(text.IndexOf('=') + 1)..] }, TargetAccess.Write, recursive: text.StartsWith("--output-directory", StringComparison.Ordinal));
            else if (text is "--output" or "--output-directory" || text == "-o" && name is "format-patch" or "archive")
            {
                if (j + 1 < rest.Count)
                    facts.Path(rest[++j], TargetAccess.Write, recursive: text != "--output" && name == "format-patch");
                else
                    facts.Unresolved($"git {name} {text} has no value");
            }
            else if (text.StartsWith("-o", StringComparison.Ordinal) && text.Length > 2 && name is "format-patch" or "archive")
                facts.Path(arg with { Text = text[2..] }, TargetAccess.Write, recursive: name == "format-patch");
            // git accepts any unique prefix of a long option ("--open" is --open-files-in-pager) and clustered short
            // options ("-nOless"); a cluster with an O in it is read as -O, which errs toward asking.
            else if (name == "grep" && (text.StartsWith("--open", StringComparison.Ordinal)
                         || text.Length > 1 && text[0] == '-' && text[1] != '-' && text.Contains('O', StringComparison.Ordinal)))
                facts.Effects |= ActionEffects.Execute;
        }

        if (GitReaders.Contains(name)
            || name is "branch" && Listing("-a", "-r", "-l", "--list", "-v", "-vv", "--show-current", "--all", "--remotes", "--merged", "--no-merged")
            || name is "tag" && Listing("-l", "--list", "-n")
            || name is "stash" && rest.Count > 0 && rest[0].Text is "list" or "show"
            || name is "worktree" && rest.Count > 0 && rest[0].Text == "list"
            || name is "config" && rest.Any(arg => arg.Text is "--get" or "--get-all" or "--list" or "-l" or "--get-regexp")
            || name is "remote" && Listing("-v", "--verbose", "show", "get-url") && !rest.Any(arg => arg.Text is "show" && rest.Count > 1))
        {
            facts.Path(repository, TargetAccess.Read, recursive: true);
            return;
        }

        if (GitNetwork.Contains(name))
        {
            facts.Effects |= ActionEffects.Network | ActionEffects.Remote;
            foreach (var arg in rest)
                if (!arg.Dynamic && (arg.Text.Contains("://", StringComparison.Ordinal) || IsRemote(arg.Text, out _) && arg.Text.Contains('@')))
                {
                    if (arg.Text.Contains("://", StringComparison.Ordinal))
                        facts.Url(arg);
                    else if (IsRemote(arg.Text, out var host))
                        facts.Host(host, arg.Display);
                }

            if (name is "push" or "ls-remote" or "remote")
                return;
        }

        if (name == "clean")
        {
            facts.Path(repository, TargetAccess.Delete, recursive: rest.Any(arg => arg.Text.StartsWith('-') && arg.Text.Contains('d')));
            return;
        }

        if (name == "config")
        {
            facts.Effects |= ActionEffects.Configuration | ActionEffects.Execute;
            if (rest.Any(arg => arg.Text is "--global" or "--system"))
                facts.Path(Field.Literal(Path.Combine(facts.Env.Home, ".gitconfig")), TargetAccess.Write);
            return;
        }

        if (!GitNetwork.Contains(name) && !KnownGitWriters.Contains(name))
        {
            // Aliases and external git-* commands run arbitrary programs.
            facts.Effects |= ActionEffects.Execute;
        }

        facts.Path(name == "clone" ? Under(null, "*") : repository, TargetAccess.Write, recursive: true);
    }

    private static readonly FrozenSet<string> KnownGitWriters = FrozenSet.Create(StringComparer.Ordinal,
        "add", "commit", "checkout", "switch", "restore", "reset", "merge", "rebase", "cherry-pick", "revert", "mv", "rm", "init",
        "stash", "apply", "am", "gc", "prune", "notes", "bisect", "worktree", "sparse-checkout", "branch", "tag", "remote",
        "format-patch", "update-index", "update-ref", "symbolic-ref", "repack", "maintenance");

    private static void Kill(CommandFacts facts, List<Field> args)
    {
        facts.Effects |= ActionEffects.Signal;
        var i = 0;
        while (i < args.Count && !args[i].Dynamic && args[i].Text.StartsWith('-') && args[i].Text != "--")
        {
            var option = args[i].Text;
            if (option is "-l" or "-L" or "--list" or "--table")
            {
                facts.Effects = ActionEffects.Read;
                return;
            }

            // "-1" after the options is the "all processes" PID; as the first word it is signal 1 only when more follow.
            if (option is "-s" or "-n" or "--signal" or "-q" or "--queue")
                i++;
            else if (option.Length > 1 && option[1..].All(char.IsAsciiDigit) && i + 1 >= args.Count)
                break;
            i++;
        }

        if (i < args.Count && !args[i].Dynamic && args[i].Text == "--")
            i++;
        foreach (var operand in args.Skip(i))
        {
            if (!operand.Dynamic && operand.Text == "--")
                continue;
            if (!operand.Dynamic && operand.Text.StartsWith('%'))
                facts.Process(operand);
            else if (operand.Dynamic || operand.Text.TrimStart('-').All(char.IsAsciiDigit))
                facts.Process(operand);
            else
                facts.ProcessName(operand);
        }
    }
}

/// <summary>Recognizes sed scripts made only of commands that read and print (no e, w, r, R or W).</summary>
internal static class SedScript
{
    public static bool IsSafe(string script)
    {
        var i = 0;
        while (true)
        {
            while (i < script.Length && (char.IsWhiteSpace(script[i]) || script[i] == ';'))
                i++;
            if (i >= script.Length)
                return true;
            if (!SkipAddress(script, ref i))
                return false;
            if (i < script.Length && script[i] == ',')
            {
                i++;
                if (!SkipAddress(script, ref i))
                    return false;
            }

            while (i < script.Length && (script[i] == ' ' || script[i] == '!'))
                i++;
            if (i >= script.Length)
                return false;
            var command = script[i++];
            switch (command)
            {
                case 'p' or 'd' or 'D' or 'n' or 'N' or 'g' or 'G' or 'h' or 'H' or 'x' or 'P' or '=' or 'l' or 'q' or 'Q' or 'z' or 'F'
                    or '{' or '}':
                    while (i < script.Length && char.IsAsciiDigit(script[i]))
                        i++;
                    break;
                case 's':
                {
                    if (i >= script.Length || script[i] is '\\' or '\n')
                        return false;
                    var delimiter = script[i++];
                    if (!SkipDelimited(script, ref i, delimiter) || !SkipDelimited(script, ref i, delimiter))
                        return false;
                    while (i < script.Length && (char.IsAsciiDigit(script[i]) || script[i] is 'g' or 'p' or 'i' or 'I' or 'm' or 'M'))
                        i++;
                    break;
                }
                case 'y':
                {
                    if (i >= script.Length)
                        return false;
                    var delimiter = script[i++];
                    if (!SkipDelimited(script, ref i, delimiter) || !SkipDelimited(script, ref i, delimiter))
                        return false;
                    break;
                }
                case '#':
                    while (i < script.Length && script[i] != '\n')
                        i++;
                    break;
                default:
                    return false;
            }

            if (i < script.Length && !(char.IsWhiteSpace(script[i]) || script[i] is ';' or '}'))
                return false;
        }
    }

    private static bool SkipAddress(string script, ref int i)
    {
        if (i >= script.Length)
            return true;
        if (script[i] == '$')
        {
            i++;
            return true;
        }

        if (char.IsAsciiDigit(script[i]))
        {
            while (i < script.Length && (char.IsAsciiDigit(script[i]) || script[i] == '~'))
                i++;
            return true;
        }

        if (script[i] == '/')
        {
            i++;
            if (!SkipDelimited(script, ref i, '/'))
                return false;
            while (i < script.Length && script[i] is 'I' or 'M')
                i++;
        }

        return true;
    }

    private static bool SkipDelimited(string script, ref int i, char delimiter)
    {
        while (i < script.Length)
        {
            var c = script[i++];
            if (c == '\\')
                i++;
            else if (c == delimiter)
                return true;
            else if (c == '\n')
                return false;
        }

        return false;
    }
}

using System.Collections.Immutable;
using System.Text;

namespace Corvid.Approvals.Shell;

internal static class LauncherTables
{
    // strace stops at its first operand (getopt "+").
    public static readonly OptionTable Strace = new("AcCdDfFhikNnqrtTvVwxyYzZ", "abeEIoOpPsSuUX",
        ["follow-forks", "output-separately", "summary-only", "summary", "summary-wall-clock", "instruction-pointer",
         "stack-traces", "syscall-number", "no-abbrev", "verbose", "version", "help", "debug", "seccomp-bpf", "kill-on-exit",
         "always-show-pid", "successful-only", "failed-only", "syscall-times", "absolute-timestamps", "relative-timestamps",
         "timestamps", "quiet", "silent", "decode-fds", "decode-pids", "daemonize", "tips"],
        ["abbrev", "attach", "trace", "trace-path", "output", "env", "signal", "status", "user", "raw", "read", "write", "inject",
         "fault", "string-limit", "summary-sort-by", "summary-columns", "columns", "detach-on", "const-print-style", "argv0",
         "interruptible", "overhead", "decode-fds", "decode-pids", "quiet", "silent", "timestamps", "syscall-times",
         "absolute-timestamps", "daemonize", "tips"]);

    // ltrace stops at its first operand.
    public static readonly OptionTable Ltrace = new("bcCfhiLrStTV", "aADeFlnopsuwx",
        ["demangle", "help", "version", "no-signals"],
        ["align", "config", "debug", "indent", "library", "output", "where"]);

    public static readonly OptionTable PerfStat = new("aAcdnqvBS", "eprtGCoIxMD",
        ["all-cpus", "no-aggr", "detailed", "null", "quiet", "verbose", "big-num", "no-big-num", "per-socket", "per-die",
         "per-core", "per-thread", "per-node", "append", "metric-only", "topdown", "no-merge", "scale", "no-inherit"],
        ["event", "pid", "tid", "repeat", "cgroup", "cpu", "output", "interval-print", "field-separator", "metrics", "delay",
         "timeout"]);

    public static readonly OptionTable PerfRecord = new("agqvsTnRBNdi", "eptCoFcmGru",
        ["all-cpus", "quiet", "verbose", "stat", "timestamp", "no-inherit", "no-buildid", "no-buildid-cache", "raw-samples",
         "data", "no-samples", "phys-data", "sample-cpu", "period"],
        ["event", "pid", "tid", "cpu", "output", "freq", "count", "mmap-pages", "cgroup", "uid", "call-graph", "filter",
         "realtime"]);

    public static readonly OptionTable PerfTrace = new("asSvT", "eptCoum",
        ["all-cpus", "summary", "summary-only", "verbose", "time", "no-inherit", "comm", "sched", "failure"],
        ["event", "expr", "pid", "tid", "cpu", "output", "uid", "mmap-pages", "duration", "max-stack", "min-stack"]);

    // util-linux nsenter: getopt "+", namespace options take an optional attached file.
    public static readonly OptionTable Nsenter = new("aFZcehV", "tSGW",
        ["all", "no-fork", "follow-context", "join-cgroup", "env", "preserve-credentials", "keep-caps", "help", "version"],
        ["target", "setuid", "setgid", "wdns"],
        ["mount", "uts", "ipc", "net", "pid", "cgroup", "user", "time", "root", "wd"],
        OptionalAttached: "muinpCUTrw");

    // util-linux unshare: getopt "+".
    public static readonly OptionTable Unshare = new("frchV", "RwSGl",
        ["fork", "map-root-user", "map-current-user", "map-auto", "keep-caps", "help", "version"],
        ["propagation", "setgroups", "root", "wd", "setuid", "setgid", "map-user", "map-group", "map-users", "map-groups",
         "monotonic", "boottime", "load-interp"],
        ["mount", "uts", "ipc", "net", "pid", "user", "cgroup", "time", "kill-child", "mount-proc", "mount-binfmt"],
        OptionalAttached: "muinpUCT");

    // systemd-run: getopt "+".
    public static readonly OptionTable SystemdRun = new("hrtPqGdS", "HMEpu",
        ["help", "version", "user", "system", "scope", "remain-after-exit", "send-sighup", "no-block", "wait", "pty", "pipe",
         "quiet", "collect", "same-dir", "shell", "no-ask-password", "on-clock-change", "on-timezone-change", "slice-inherit"],
        ["host", "machine", "unit", "property", "description", "slice", "service-type", "uid", "gid", "nice",
         "working-directory", "setenv", "on-active", "on-boot", "on-startup", "on-unit-active", "on-unit-inactive",
         "on-calendar", "timer-property", "path-property", "socket-property", "expand-environment"]);

    // util-linux script permutes (its file operand may come first).
    public static readonly OptionTable Script = new("aefqhV", "cEIOBTmo",
        ["append", "return", "flush", "quiet", "help", "version", "force"],
        ["command", "echo", "log-in", "log-out", "log-io", "log-timing", "logging-format", "output-limit"],
        ["timing"], OptionalAttached: "t", Permute: true);

    // GNU parallel: Getopt::Long with bundling and require_order, so it stops at the command.
    public static readonly OptionTable Parallel = new("kuvt0", "ja",
        ["keep-order", "ungroup", "verbose", "null", "will-cite", "bar", "eta", "progress", "line-buffer", "tag", "group",
         "dry-run", "no-notice"],
        ["jobs", "halt", "timeout", "retries", "delay", "joblog", "results", "arg-file"]);

    // Unit properties that only limit resources. Others can run more commands (ExecStartPre=), change the user, the root
    // directory or the mounts, so a -p outside this list is refused.
    public static readonly ImmutableHashSet<string> SystemdResourceProperties = ImmutableHashSet.Create(StringComparer.Ordinal,
        "MemoryMax", "MemoryHigh", "MemoryLow", "MemoryMin", "MemorySwapMax", "CPUQuota", "CPUWeight", "IOWeight", "TasksMax",
        "Nice", "RuntimeMaxSec", "TimeoutStartSec", "TimeoutStopSec", "CPUSchedulingPolicy");
}

/// <summary>
/// Launchers that run a command under a tracer, in other namespaces or a sandbox, as a unit of the service manager, or from
/// a shell string. Each is unwrapped like the plain wrappers, so the command it runs meets the hardline list, target
/// extraction and <c>risk</c> exactly as if it ran on its own. Unwrapping never lowers risk: a tracer adds the files it
/// writes and the processes it attaches to, and a launcher that changes namespaces, the root directory or the user marks
/// everything it runs as unresolved, because its paths and program names may not mean what they say.
/// </summary>
internal sealed partial class ShellAnalyzer
{
    private const int MaxParallelJobs = 64;
    private const string ParallelInput = "__corvid_parallel_input";

    private static Scope Isolate(Scope scope, string verb) => scope with
    {
        Via = scope.Via.Add(verb),
        Opaque = scope.Opaque ?? $"runs under {verb}, which changes what paths and program names refer to",
    };

    /// <summary>The launcher's own command: what its options read, write or attach to.</summary>
    /// <param name="redirects">The command's redirections, when no inner command carries them.</param>
    private void Launcher(string verb, Scope scope, string text, Action<CommandFacts> describe,
        ImmutableArray<(Field Target, bool Writes)> redirects = default)
    {
        var facts = NewFacts(verb, scope);
        describe(facts);
        facts.AddRedirects(redirects);
        Emit(facts, text);
    }

    private static Field? Option(List<(string Name, Field? Value)> options, params string[] names) =>
        options.LastOrDefault(option => names.Contains(option.Name)).Value;

    private static bool Has(List<(string Name, Field? Value)> options, params string[] names) =>
        options.Any(option => names.Contains(option.Name));

    private static void Pids(CommandFacts facts, Field value)
    {
        if (value.Dynamic)
        {
            facts.Process(value);
            return;
        }

        foreach (var pid in value.Text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
            facts.Process(Field.Literal(pid));
    }

    /// <summary>Variables set for the program (<c>strace -E</c>, <c>systemd-run -E</c>); a code-loading one makes it opaque.</summary>
    private static Scope WithEnvironment(Scope scope, IEnumerable<Field> settings)
    {
        foreach (var setting in settings)
        {
            var equals = setting.Text.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? setting.Text : setting.Text[..equals];
            if (name.Length == 0 || setting.Dynamic && equals < 0)
            {
                scope = scope with { Opaque = scope.Opaque ?? $"sets the variable '{setting.Display}', computed at run time" };
                continue;
            }

            scope = scope with
            {
                Variables = scope.Variables.SetItem(name, equals < 0 || setting.Dynamic ? null : setting.Text[(equals + 1)..]),
                Exported = scope.Exported.Add(name),
                Opaque = scope.Opaque ?? (IsCodeVariable(name) ? $"runs with {name} set, which can change what runs" : null),
            };
        }

        return scope;
    }

    // strace [options] [COMMAND [ARGS]]: -o writes a file (or pipes to a shell command), -p attaches to processes,
    // -u runs the command as another user, -E sets its environment, and inject or fault expressions change what it does.
    private StateChange? Strace(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        if (!Options.Parse(args, LauncherTables.Strace, out var options, out var operand, out var problem))
            return Refuse("strace", scope, extras, text, $"strace: {problem}");
        return Tracer("strace", args.Skip(operand).ToList(), options, scope, extras, nesting, text,
            separate: options.Count(option => option.Name is "f") >= 2 || Has(options, "output-separately"));
    }

    // ltrace [options] [COMMAND [ARGS]]: -o writes a file, -p attaches, -u runs as another user, -F reads a configuration.
    private StateChange? Ltrace(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        if (!Options.Parse(args, LauncherTables.Ltrace, out var options, out var operand, out var problem))
            return Refuse("ltrace", scope, extras, text, $"ltrace: {problem}");
        return Tracer("ltrace", args.Skip(operand).ToList(), options, scope, extras, nesting, text, separate: false);
    }

    private StateChange? Tracer(string verb, List<Field> inner, List<(string Name, Field? Value)> options, Scope scope, Extras extras,
        int nesting, string text, bool separate)
    {
        var user = Option(options, "u", "user");
        var innerScope = user is null ? Via(scope, verb) : Elevate(scope, $"{verb} -u");
        innerScope = WithEnvironment(innerScope, options.Where(option => option.Name is "E" or "env" && verb == "strace")
            .Select(option => option.Value!));
        string? pipe = null;
        Launcher(verb, user is null ? scope : Elevate(scope, $"{verb} -u"), text, facts =>
        {
            facts.Effects |= ActionEffects.Read;
            foreach (var (name, value) in options)
            {
                if (value is null)
                    continue;
                switch (name)
                {
                    case "o" or "output":
                        if (value is { Dynamic: false, Text: ['|' or '!', ..] })
                            pipe = value.Text[1..];
                        else
                        {
                            facts.Path(value, TargetAccess.Write);
                            // -ff writes one file per process, named FILE.PID.
                            if (separate && !value.Dynamic)
                                facts.Path(new Field(value.Text + ".*", false, true, value.Display + ".<pid>"), TargetAccess.Write);
                        }

                        break;
                    case "p" or "attach":
                        Pids(facts, value);
                        break;
                    case "F" or "config" when verb == "ltrace":
                        facts.Path(value, TargetAccess.Read);
                        break;
                    case "e" or "inject" or "fault" when verb == "strace":
                        if (value.Dynamic || name != "e" || value.Text.StartsWith("inject", StringComparison.Ordinal)
                            || value.Text.StartsWith("fault", StringComparison.Ordinal))
                        {
                            facts.Effects |= ActionEffects.Execute;
                            facts.Unresolved("strace injects faults or return values, which changes what the traced program does");
                        }

                        break;
                }
            }
        }, inner.Count == 0 ? extras.Redirects : default);
        if (pipe is not null)
            NestedScript($"{verb} -o", pipe, ChildShell(Via(scope, $"{verb} -o |")), nesting, text);
        if (inner.Count > 0)
            Invocation(inner, innerScope, extras, nesting, text);
        return null;
    }

    // valgrind [options] PROGRAM [ARGS]. Its options are single words (--name=value) before the program, so the first word
    // that is not an option is the program; any option not listed here is refused.
    private static readonly ImmutableHashSet<string> ValgrindFlags = ImmutableHashSet.Create(StringComparer.Ordinal,
        "-q", "--quiet", "-v", "--verbose", "-h", "--help", "--help-debug", "--version", "-d");

    private static readonly ImmutableHashSet<string> ValgrindSettings = ImmutableHashSet.Create(StringComparer.Ordinal,
        "tool", "leak-check", "show-leak-kinds", "track-origins", "error-exitcode", "num-callers", "trace-children",
        "child-silent-after-fork", "show-reachable", "errors-for-leak-kinds", "undef-value-errors", "keep-stacktraces",
        "read-var-info", "read-inline-info", "demangle", "time-stamp", "track-fds", "max-stackframe", "main-stacksize",
        "smc-check", "fair-sched", "log-fd", "xml", "gen-suppressions", "leak-resolution", "show-possibly-lost",
        "freelist-vol", "partial-loads-ok", "expensive-definedness-checks", "show-mismatched-frees", "error-limit",
        "show-error-list", "s", "max-threads", "branch-sim", "cache-sim", "dump-instr", "collect-jumps", "heap", "stacks",
        "pages-as-heap", "depth", "threshold", "time-unit", "history-level", "conflict-cache-size", "check-stack-var",
        "suppressions", "log-file", "xml-file", "massif-out-file", "callgrind-out-file", "cachegrind-out-file", "dhat-out-file",
        "log-socket", "xml-socket");

    private static readonly ImmutableDictionary<string, string> ValgrindOutputs = new Dictionary<string, string>
    {
        ["massif"] = "massif.out", ["callgrind"] = "callgrind.out", ["cachegrind"] = "cachegrind.out", ["dhat"] = "dhat.out",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    private StateChange? Valgrind(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        var settings = new List<(string Name, Field Value)>();
        var i = 0;
        for (; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Dynamic)
            {
                if (arg.Text.StartsWith('-') || arg.Text.Length == 0)
                    return Refuse("valgrind", scope, extras, text, $"valgrind: option '{arg.Display}' is computed at run time");
                break;
            }

            if (!arg.Text.StartsWith('-'))
                break;
            if (ValgrindFlags.Contains(arg.Text))
                continue;
            var equals = arg.Text.IndexOf('=', StringComparison.Ordinal);
            if (!arg.Text.StartsWith("--", StringComparison.Ordinal) || equals < 0 || !ValgrindSettings.Contains(arg.Text[2..equals]))
                return Refuse("valgrind", scope, extras, text, $"valgrind: unknown option '{arg.Text}'");
            settings.Add((arg.Text[2..equals], Field.Literal(arg.Text[(equals + 1)..])));
        }

        Launcher("valgrind", scope, text, facts =>
        {
            // Valgrind also reads options from ~/.valgrindrc, ./.valgrindrc and $VALGRIND_OPTS, which Corvid does not see.
            facts.Effects |= ActionEffects.Execute;
            foreach (var (name, value) in settings)
                switch (name)
                {
                    case "suppressions":
                        facts.Path(value, TargetAccess.Read);
                        break;
                    case "log-file" or "xml-file" or "massif-out-file" or "callgrind-out-file" or "cachegrind-out-file" or "dhat-out-file":
                        facts.Path(ValgrindFileName(value.Text), TargetAccess.Write);
                        break;
                    case "log-socket" or "xml-socket":
                        var colon = value.Text.LastIndexOf(':');
                        facts.Host(colon > 0 ? value.Text[..colon] : value.Text, value.Text);
                        break;
                }

            // The profiling tools write NAME.out.PID in the working directory unless told otherwise.
            if (settings.LastOrDefault(setting => setting.Name == "tool").Value?.Text is { } tool
                && ValgrindOutputs.TryGetValue(tool, out var output) && !settings.Any(setting => setting.Name == $"{tool}-out-file"))
                facts.Path(new Field(output + ".*", false, true, output + ".<pid>"), TargetAccess.Write);
        }, i == args.Count ? extras.Redirects : default);
        var inner = args.Skip(i).ToList();
        if (inner.Count > 0)
            Invocation(inner, Via(scope, "valgrind"), extras, nesting, text);
        return null;
    }

    // Valgrind expands %p (the PID) and %n (a sequence number) in file names, and %q{VAR} from the environment.
    private static Field ValgrindFileName(string name)
    {
        var variable = name.IndexOf("%q", StringComparison.Ordinal);
        if (variable >= 0)
            return new Field(name[..variable], true, false, name);
        var pattern = name.Replace("%p", "*", StringComparison.Ordinal).Replace("%n", "*", StringComparison.Ordinal);
        return new Field(pattern, false, pattern != name, name);
    }

    // perf stat|record|trace [options] [COMMAND [ARGS]]. Other subcommands keep the description of a program that runs code.
    private StateChange? Perf(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        var start = args.Count > 0 && !args[0].Dynamic && args[0].Text == "--no-pager" ? 1 : 0;
        var subcommand = start < args.Count && !args[start].Dynamic ? args[start].Text : null;
        var table = subcommand switch
        {
            "stat" => LauncherTables.PerfStat,
            "record" => LauncherTables.PerfRecord,
            "trace" => LauncherTables.PerfTrace,
            _ => null,
        };
        if (table is null)
        {
            var facts = NewFacts("perf", scope);
            Verbs.Describe(facts, "perf", args);
            facts.AddRedirects(extras.Redirects);
            Emit(facts, text);
            return null;
        }

        var rest = args.Skip(start + 1).ToList();
        if (!Options.Parse(rest, table, out var options, out var operand, out var problem))
            return Refuse("perf", scope, extras, text, $"perf {subcommand}: {problem}");
        Launcher("perf", scope, text, facts =>
        {
            facts.Subcommand = subcommand;
            facts.Effects |= ActionEffects.Read;
            var output = Option(options, "o", "output");
            if (output is not null && !(output is { Dynamic: false, Text: "-" }))
                facts.Path(output, TargetAccess.Write);
            else if (output is null && subcommand == "record")
            {
                // perf record writes perf.data and renames an existing one to perf.data.old.
                facts.Path(Field.Literal("perf.data"), TargetAccess.Write);
                facts.Path(Field.Literal("perf.data.old"), TargetAccess.Write);
            }

            foreach (var (name, value) in options)
                if (name is "p" or "t" or "pid" or "tid" && value is not null)
                    Pids(facts, value);
            // System-wide profiling sees every process on the machine.
            if (Has(options, "a", "all-cpus", "C", "cpu"))
                facts.Effects |= ActionEffects.System;
        }, operand == rest.Count ? extras.Redirects : default);
        var inner = rest.Skip(operand).ToList();
        if (inner.Count > 0)
            Invocation(inner, Via(scope, $"perf {subcommand}"), extras, nesting, text);
        return null;
    }

    // nsenter [options] [PROGRAM [ARGS]]: runs in another process's namespaces, where paths and users may differ.
    private StateChange? Nsenter(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        if (!Options.Parse(args, LauncherTables.Nsenter, out var options, out var operand, out var problem))
            return Refuse("nsenter", scope, extras, text, $"nsenter: {problem}");
        var inner = args.Skip(operand).ToList();
        if (inner.Count == 0)
            return Refuse("nsenter", scope, extras, text, "nsenter without a program starts an interactive shell");
        var target = Option(options, "t", "target");
        var innerScope = Isolate(scope, "nsenter");
        if (Has(options, "r", "root", "w", "wd", "W", "wdns"))
            innerScope = innerScope with { Cwds = [null] };
        Launcher("nsenter", scope, text, facts =>
        {
            facts.Effects |= ActionEffects.Execute;
            facts.Unresolved(target is null
                ? "nsenter runs the command in other namespaces"
                : $"nsenter runs the command in the namespaces of process {target.Display}");
            foreach (var (name, value) in options)
                if (value is not null && name is "m" or "u" or "i" or "n" or "p" or "C" or "U" or "T" or "mount" or "uts" or "ipc" or "net"
                        or "pid" or "cgroup" or "user" or "time")
                    facts.Path(value, TargetAccess.Read);
        });
        Invocation(inner, innerScope, extras, nesting, text);
        return null;
    }

    // unshare [options] [PROGRAM [ARGS]]: runs in new namespaces, perhaps as root in a new user namespace or another root.
    private StateChange? Unshare(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        if (!Options.Parse(args, LauncherTables.Unshare, out var options, out var operand, out var problem))
            return Refuse("unshare", scope, extras, text, $"unshare: {problem}");
        var inner = args.Skip(operand).ToList();
        if (inner.Count == 0)
            return Refuse("unshare", scope, extras, text, "unshare without a program starts an interactive shell");
        var innerScope = Isolate(scope, "unshare");
        if (Has(options, "R", "root"))
            innerScope = innerScope with { Cwds = [null] };
        else if (Option(options, "w", "wd") is { } directory)
            innerScope = innerScope with { Cwds = directory.Dynamic ? [null] : NewFacts("unshare", scope).Directories(directory) };
        Launcher("unshare", scope, text, facts =>
        {
            facts.Effects |= ActionEffects.Execute;
            facts.Unresolved("unshare runs the command in new namespaces");
            foreach (var (name, value) in options)
                if (value is not null && name is "l" or "load-interp")
                    facts.Path(value, TargetAccess.Execute);
        });
        Invocation(inner, innerScope, extras, nesting, text);
        return null;
    }

    // firejail [options] [PROGRAM [ARGS]]: options are single words; only those listed here are accepted.
    private static readonly ImmutableHashSet<string> FirejailFlags = ImmutableHashSet.Create(StringComparer.Ordinal,
        "--quiet", "--noprofile", "--private", "--private-tmp", "--private-dev", "--private-cache", "--nonewprivs", "--noroot",
        "--nogroups", "--nosound", "--novideo", "--no3d", "--nodvd", "--notv", "--nou2f", "--seccomp", "--apparmor",
        "--caps.drop=all", "--net=none", "--x11=none", "--machine-id", "--disable-mnt");

    private static readonly ImmutableHashSet<string> FirejailSettings = ImmutableHashSet.Create(StringComparer.Ordinal,
        "profile", "private", "whitelist", "blacklist", "read-only", "noexec", "tmpfs", "name", "timeout", "hostname", "dns");

    private StateChange? Firejail(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        var settings = new List<(string Name, Field Value)>();
        var i = 0;
        for (; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Dynamic)
            {
                if (arg.Text.StartsWith('-') || arg.Text.Length == 0)
                    return Refuse("firejail", scope, extras, text, $"firejail: option '{arg.Display}' is computed at run time");
                break;
            }

            if (arg.Text == "--")
            {
                i++;
                break;
            }

            if (!arg.Text.StartsWith('-'))
                break;
            if (FirejailFlags.Contains(arg.Text))
                continue;
            var equals = arg.Text.IndexOf('=', StringComparison.Ordinal);
            if (!arg.Text.StartsWith("--", StringComparison.Ordinal) || equals < 0 || !FirejailSettings.Contains(arg.Text[2..equals]))
                return Refuse("firejail", scope, extras, text, $"firejail: unknown option '{arg.Text}'");
            settings.Add((arg.Text[2..equals], Field.Literal(arg.Text[(equals + 1)..])));
        }

        var inner = args.Skip(i).ToList();
        if (inner.Count == 0)
            return Refuse("firejail", scope, extras, text, "firejail without a program starts an interactive shell");
        Launcher("firejail", scope, text, facts =>
        {
            facts.Effects |= ActionEffects.Execute;
            facts.Unresolved("firejail runs the command in a sandbox with its own mounts and profile");
            foreach (var (name, value) in settings)
                if (name == "profile")
                    facts.Path(value, TargetAccess.Read);
                else if (name == "private")
                    facts.Path(value, TargetAccess.Write);
        });
        Invocation(inner, Isolate(scope, "firejail"), extras, nesting, text);
        return null;
    }

    // bwrap [options] COMMAND [ARGS]: every option takes a fixed number of separate words.
    private static readonly ImmutableDictionary<string, int> BwrapArity = new Dictionary<string, int>
    {
        ["--help"] = 0, ["--version"] = 0, ["--unshare-all"] = 0, ["--share-net"] = 0, ["--unshare-user"] = 0,
        ["--unshare-user-try"] = 0, ["--unshare-ipc"] = 0, ["--unshare-pid"] = 0, ["--unshare-net"] = 0, ["--unshare-uts"] = 0,
        ["--unshare-cgroup"] = 0, ["--unshare-cgroup-try"] = 0, ["--clearenv"] = 0, ["--new-session"] = 0,
        ["--die-with-parent"] = 0, ["--as-pid-1"] = 0, ["--disable-userns"] = 0, ["--assert-userns-disabled"] = 0,
        ["--argv0"] = 1, ["--userns"] = 1, ["--userns2"] = 1, ["--pidns"] = 1, ["--uid"] = 1, ["--gid"] = 1, ["--hostname"] = 1,
        ["--chdir"] = 1, ["--unsetenv"] = 1, ["--lock-file"] = 1, ["--sync-fd"] = 1, ["--remount-ro"] = 1, ["--exec-label"] = 1,
        ["--file-label"] = 1, ["--proc"] = 1, ["--dev"] = 1, ["--tmpfs"] = 1, ["--mqueue"] = 1, ["--dir"] = 1, ["--seccomp"] = 1,
        ["--add-seccomp-fd"] = 1, ["--block-fd"] = 1, ["--userns-block-fd"] = 1, ["--info-fd"] = 1, ["--json-status-fd"] = 1,
        ["--cap-add"] = 1, ["--cap-drop"] = 1, ["--perms"] = 1, ["--size"] = 1, ["--overlay-src"] = 1, ["--tmp-overlay"] = 1,
        ["--ro-overlay"] = 1,
        ["--setenv"] = 2, ["--bind"] = 2, ["--bind-try"] = 2, ["--dev-bind"] = 2, ["--dev-bind-try"] = 2, ["--ro-bind"] = 2,
        ["--ro-bind-try"] = 2, ["--file"] = 2, ["--bind-data"] = 2, ["--ro-bind-data"] = 2, ["--symlink"] = 2, ["--chmod"] = 2,
        ["--bind-fd"] = 2, ["--ro-bind-fd"] = 2,
        ["--overlay"] = 3,
    }.ToImmutableDictionary(StringComparer.Ordinal);

    private StateChange? Bwrap(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        var options = new List<(string Name, List<Field> Values)>();
        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            if (arg.Dynamic)
            {
                if (arg.Text.StartsWith('-') || arg.Text.Length == 0)
                    return Refuse("bwrap", scope, extras, text, $"bwrap: option '{arg.Display}' is computed at run time");
                break;
            }

            if (arg.Text == "--")
            {
                i++;
                break;
            }

            if (!arg.Text.StartsWith('-'))
                break;
            if (arg.Text == "--args")
                return Refuse("bwrap", scope, extras, text, "bwrap --args reads more options from a file descriptor");
            if (!BwrapArity.TryGetValue(arg.Text, out var arity))
                return Refuse("bwrap", scope, extras, text, $"bwrap: unknown option '{arg.Text}'");
            if (i + arity >= args.Count)
                return Refuse("bwrap", scope, extras, text, $"bwrap: option '{arg.Text}' needs {arity} values");
            options.Add((arg.Text, args.GetRange(i + 1, arity)));
            i += arity + 1;
        }

        var inner = args.Skip(i).ToList();
        if (inner.Count == 0)
            return Refuse("bwrap", scope, extras, text, "bwrap without a command");
        Launcher("bwrap", scope, text, facts =>
        {
            facts.Effects |= ActionEffects.Execute;
            facts.Unresolved("bwrap runs the command with its own mounts, where paths may name other files");
            foreach (var (name, values) in options)
                switch (name)
                {
                    // A writable bind hands the sandbox the whole tree at its source.
                    case "--bind" or "--bind-try" or "--dev-bind" or "--dev-bind-try":
                        facts.Path(values[0], TargetAccess.Write);
                        facts.Path(values[0], TargetAccess.Read, recursive: true);
                        break;
                    case "--ro-bind" or "--ro-bind-try" or "--overlay-src":
                        facts.Path(values[0], TargetAccess.Read, recursive: true);
                        break;
                    case "--overlay":
                        facts.Path(values[0], TargetAccess.Write);
                        facts.Path(values[1], TargetAccess.Write);
                        break;
                }
        });
        // The command runs in the sandbox's working directory (its own, or / when that is not mounted).
        var innerScope = Isolate(scope, "bwrap") with { Cwds = [null] };
        foreach (var (name, values) in options)
            if (name == "--clearenv")
                innerScope = innerScope with
                {
                    Variables = innerScope.Variables.ToImmutableDictionary(pair => pair.Key, _ => (string?)null, StringComparer.Ordinal),
                };
            else if (name == "--setenv" && !values[0].Dynamic)
                innerScope = WithEnvironment(innerScope,
                    [new Field(values[0].Text + "=" + values[1].Text, values[1].Dynamic, false, values[0].Text + "=" + values[1].Display)]);
            else if (name == "--unsetenv" && !values[0].Dynamic)
                innerScope = innerScope with { Variables = innerScope.Variables.SetItem(values[0].Text, null) };
        Invocation(inner, innerScope, extras, nesting, text);
        return null;
    }

    // systemd-run [options] COMMAND [ARGS]: runs the command as a unit of the service manager, as root for the system
    // manager, in a fresh environment and working directory, perhaps later (timers); with --scope it runs here, as the caller.
    private StateChange? SystemdRun(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        if (!Options.Parse(args, LauncherTables.SystemdRun, out var options, out var operand, out var problem))
            return Refuse("systemd-run", scope, extras, text, $"systemd-run: {problem}");
        if (Has(options, "H", "host"))
            return Refuse("systemd-run", scope, extras, text, "systemd-run --host runs the command on another machine");
        if (Has(options, "M", "machine"))
            return Refuse("systemd-run", scope, extras, text, "systemd-run --machine runs the command in a container");
        if (Has(options, "S", "shell"))
            return Refuse("systemd-run", scope, extras, text, "systemd-run --shell starts an interactive shell");
        if (Has(options, "timer-property", "path-property", "socket-property"))
            return Refuse("systemd-run", scope, extras, text, "systemd-run: trigger unit properties are not analysed");
        static bool Limits(Field? property) => property is { Dynamic: false } && property.Text.IndexOf('=', StringComparison.Ordinal) is var equals
            && equals > 0 && LauncherTables.SystemdResourceProperties.Contains(property.Text[..equals]);
        foreach (var (name, value) in options)
            if (name is "p" or "property" && !Limits(value))
                return Refuse("systemd-run", scope, extras, text,
                    $"systemd-run: the unit property '{value?.Display}' may run other commands or change the user, root or mounts");
        var inner = args.Skip(operand).ToList();
        if (inner.Count == 0)
            return Refuse("systemd-run", scope, extras, text, "systemd-run without a command");

        var user = Has(options, "user");
        var scoped = Has(options, "scope");
        var uid = Option(options, "uid");
        var elevated = !user && !scoped || uid is not null;
        var launcherScope = elevated ? scope with { Elevated = true } : scope;
        var innerScope = Isolate(launcherScope, "systemd-run");
        if (!scoped)
        {
            // A service gets the manager's environment, not this shell's, and runs in / (system) or the home directory (user).
            innerScope = innerScope with
            {
                Variables = innerScope.Variables.ToImmutableDictionary(pair => pair.Key,
                    pair => user && uid is null && pair.Key == "HOME" ? pair.Value : null, StringComparer.Ordinal),
                Exported = innerScope.Exported.Clear(),
            };
            if (!Has(options, "d", "same-dir"))
                innerScope = innerScope with { Cwds = [user && uid is null ? _env.Home : "/"] };
        }

        if (Option(options, "working-directory") is { } directory)
            innerScope = innerScope with { Cwds = directory.Dynamic ? [null] : NewFacts("systemd-run", scope).Directories(directory) };
        innerScope = WithEnvironment(innerScope, options.Where(option => option.Name is "E" or "setenv").Select(option => option.Value!));

        // The service manager expands ${VAR} and $VAR in the command (and % specifiers in a unit), unless told not to.
        var expand = Option(options, "expand-environment")?.Text is { } setting
            ? setting is "yes" or "true" or "1" or "on"
            : !scoped;
        if (expand)
            inner = inner.Select(field => !field.Dynamic && field.Text.AsSpan().IndexOfAny('$', '%') is >= 0 and var at
                ? new Field(field.Text[..at], true, false, field.Display)
                : field).ToList();

        Launcher("systemd-run", launcherScope, text, facts =>
        {
            facts.Effects |= ActionEffects.Execute | ActionEffects.System;
            facts.Unresolved(scoped
                ? "systemd-run runs the command as a transient scope of the service manager"
                : "systemd-run runs the command as a service, with the manager's environment and working directory");
            if (options.Any(option => option.Name.StartsWith("on-", StringComparison.Ordinal)))
                facts.Effects |= ActionEffects.Schedule;
        });
        if (inner[0].Dynamic)
            return Refuse("systemd-run", innerScope, extras, text, $"systemd-run: the program '{inner[0].Display}' is expanded by the service manager");
        Invocation(inner, innerScope, extras, nesting, text);
        return null;
    }

    // script [options] [FILE]: records a terminal session to FILE (default ./typescript); -c runs a command with $SHELL -c.
    private StateChange? ScriptCommand(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        if (!Options.Parse(args, LauncherTables.Script, out var options, out var operand, out var problem))
            return Refuse("script", scope, extras, text, $"script: {problem}");
        var operands = args.Skip(operand).ToList();
        if (operands.Count > 1)
            return Refuse("script", scope, extras, text, "script takes one file");
        var command = Option(options, "c", "command");
        if (command is null)
            return Refuse("script", scope, extras, text, "script without -c starts an interactive shell");

        Launcher("script", scope, text, facts =>
        {
            facts.Path(operands.Count == 1 ? operands[0] : Field.Literal("typescript"), TargetAccess.Write);
            foreach (var (name, value) in options)
                if (value is not null && name is "I" or "O" or "B" or "T" or "t" or "log-in" or "log-out" or "log-io" or "log-timing" or "timing")
                    facts.Path(value, TargetAccess.Write);
            facts.AddRedirects(extras.Redirects);
            // script runs the command with $SHELL, or /bin/sh when it is not set.
            if (scope.Variables.TryGetValue("SHELL", out var shell) && (shell is null || !Shells.Contains(Path.GetFileName(shell))))
                facts.Unresolved($"script runs the command with $SHELL ({shell ?? "unknown"}), which may read it differently from bash");
        });
        if (command.Dynamic)
            return Refuse("script", scope, Extras.None, text, "script -c: the command is computed at run time");
        NestedScript("script", command.Text, ChildShell(Via(scope, "script -c")), nesting, text);
        return null;
    }

    // GNU parallel [options] [COMMAND] (::: ARGS | :::: FILES)...: COMMAND is joined into a shell string with each input
    // substituted for its replacement strings ({}, {.}, {/}, {//}, {/.}, {#}, {N}), or appended when it has none. Static
    // inputs are substituted exactly, one job at a time; inputs from stdin or files stand in as values unknown until run.
    private StateChange? ParallelCommand(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        if (!Options.Parse(args, LauncherTables.Parallel, out var options, out var operand, out var problem))
            return Refuse("parallel", scope, extras, text, $"parallel: {problem}");
        var rest = args.Skip(operand).ToList();
        static bool IsSeparator(Field field) => !field.Dynamic && field.Text is ":::" or "::::" or ":::+" or "::::+";
        var first = rest.FindIndex(IsSeparator);
        var words = first < 0 ? rest : rest.Take(first).ToList();

        var sources = new List<List<Field>?>();
        var files = new List<Field>();
        for (var i = first; i >= 0 && i < rest.Count;)
        {
            var separator = rest[i].Text;
            var next = rest.FindIndex(i + 1, IsSeparator);
            var values = rest.GetRange(i + 1, (next < 0 ? rest.Count : next) - i - 1);
            if (separator is ":::+" or "::::+")
                return Refuse("parallel", scope, extras, text, $"parallel: linked inputs ({separator}) are not analysed");
            if (separator == "::::")
            {
                files.AddRange(values);
                sources.AddRange(values.Select(_ => (List<Field>?)null));
            }
            else
                sources.Add(values);
            i = next;
        }

        foreach (var (name, value) in options)
            if (name is "a" or "arg-file" && value is not null)
            {
                files.Add(value);
                sources.Add(null);
            }

        if (sources.Count == 0)
            sources.Add(null); // stdin
        Launcher("parallel", scope, text, facts =>
        {
            facts.Effects |= ActionEffects.Read;
            foreach (var file in files)
                facts.Path(file, TargetAccess.Read);
            if (Option(options, "joblog") is { } log)
                facts.Path(log, TargetAccess.Write);
            if (Option(options, "results") is { } results)
                facts.Path(results, TargetAccess.Write, recursive: true);
        });

        if (words.Any(word => word.Dynamic))
            return Refuse("parallel", scope, extras, text, "parallel: the command is computed at run time");
        var template = string.Join(' ', words.Select(word => word.Text));
        var child = ChildShell(Via(scope, "parallel"));
        var known = sources.All(source => source is not null && source.All(value => !value.Dynamic));

        if (!known)
        {
            // Unknown input: each replacement string is a value known only at run time.
            if (words.Count == 0)
                return Refuse("parallel", scope, extras, text, "parallel runs commands read from its input");
            if (template.AsSpan().IndexOfAny('\'', '\\') >= 0)
                return Refuse("parallel", scope, extras, text, "parallel: quoting around replacement strings is not analysed");
            if (Substitute(template, _ => "$" + ParallelInput, out var script) is { } unknown)
                return Refuse("parallel", scope, extras, text, $"parallel: {unknown}");
            NestedScript("parallel", script, child with { Variables = child.Variables.Remove(ParallelInput) }, nesting, text,
                extras.Redirects);
            return null;
        }

        if (template.Contains("{%}", StringComparison.Ordinal) && template.AsSpan().IndexOfAny('\'', '\\') >= 0)
            return Refuse("parallel", scope, extras, text, "parallel: quoting around replacement strings is not analysed");
        var jobs = new List<List<string>> { new() };
        foreach (var source in sources)
        {
            jobs = [.. jobs.SelectMany(job => source!.Select(value => (List<string>)[.. job, value.Text]))];
            if (jobs.Count > MaxParallelJobs)
                return Refuse("parallel", scope, extras, text, $"parallel: more than {MaxParallelJobs} jobs");
        }

        for (var job = 0; job < jobs.Count; job++)
        {
            var values = jobs[job];
            string script;
            if (words.Count == 0)
                script = string.Join(' ', values);
            else if (Substitute(template, token => Replacement(token, values, job + 1), out script) is { } unknown)
                return Refuse("parallel", scope, extras, text, $"parallel: {unknown}");
            NestedScript("parallel", script, child, nesting, text, job == 0 ? extras.Redirects : default);
        }

        return null;
    }

    /// <summary>
    /// Replaces GNU parallel's replacement strings in a command template, or appends the input when there are none.
    /// Returns why the template cannot be read exactly (a Perl expression, a form not handled), or null.
    /// </summary>
    private static string? Substitute(string template, Func<string, string> replace, out string script)
    {
        var builder = new StringBuilder();
        var replaced = false;
        script = "";
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
            {
                builder.Append(template[i]);
                continue;
            }

            var close = template.IndexOf('}', i + 1);
            var token = close < 0 ? "" : template[(i + 1)..close];
            if (token.Length > 0 && token[0] is '=' or '+')
                return $"the replacement string '{{{token}}}' is not analysed";
            if (close < 0 || !IsReplacement(token))
            {
                if (token.Length > 0 && (char.IsAsciiDigit(token[0]) || token[0] is '.' or '/' or '#' or '%'))
                    return $"the replacement string '{{{token}}}' is not analysed";
                builder.Append('{');
                continue;
            }

            builder.Append(replace(token));
            replaced = true;
            i = close;
        }

        if (!replaced)
            builder.Append(' ').Append(replace(""));
        script = builder.ToString();
        return null;
    }

    private static bool IsReplacement(string token)
    {
        var digits = token.TakeWhile(char.IsAsciiDigit).Count();
        return token[digits..] is "" or "." or "/" or "//" or "/." || token is "#" or "%";
    }

    // One value, as GNU parallel would put it in the command: transformed, then quoted for the shell.
    private static string Replacement(string token, List<string> values, int job)
    {
        if (token == "#")
            return job.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (token == "%")
            return "$" + ParallelInput;
        var digits = token.TakeWhile(char.IsAsciiDigit).Count();
        var form = token[digits..];
        var selected = digits == 0 ? values
            : int.Parse(token[..digits], System.Globalization.CultureInfo.InvariantCulture) is var n and >= 1 && n <= values.Count ? [values[n - 1]]
            : [];
        return string.Join(' ', selected.Select(value => Quote(form switch
        {
            "." => WithoutExtension(value),
            "/" => value.TrimEnd('/') is var trimmed && trimmed.LastIndexOf('/') is var slash and >= 0 ? trimmed[(slash + 1)..] : value,
            "//" => value.LastIndexOf('/') is var slash and >= 0 ? (slash == 0 ? "/" : value[..slash]) : ".",
            "/." => WithoutExtension(value.LastIndexOf('/') is var slash and >= 0 ? value[(slash + 1)..] : value),
            _ => value,
        })));
    }

    private static string WithoutExtension(string value)
    {
        var dot = value.LastIndexOf('.');
        var slash = value.LastIndexOf('/');
        return dot > slash + 1 ? value[..dot] : value;
    }

    private static string Quote(string value) => value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '+' or ',' or ':' or '@')
        ? value
        : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

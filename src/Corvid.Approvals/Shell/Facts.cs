using System.Collections.Immutable;

namespace Corvid.Approvals.Shell;

/// <summary>A program's options: flags and value-taking options, short and long.</summary>
/// <param name="OptionalAttached">Short options whose value is optional and only ever attached (<c>xargs -i[R]</c>).</param>
/// <param name="Permute">GNU-style: options may follow operands (<c>su root -c cmd</c>).</param>
internal sealed record OptionTable(
    string Flags = "",
    string WithValue = "",
    string[]? Long = null,
    string[]? LongWithValue = null,
    string[]? LongOptional = null,
    string OptionalAttached = "",
    bool Permute = false)
{
    public static readonly OptionTable Empty = new();

    public bool IsLongFlag(string name) => Long?.Contains(name) == true;

    public bool IsLongWithValue(string name) => LongWithValue?.Contains(name) == true;

    public bool IsLongOptional(string name) => LongOptional?.Contains(name) == true;
}

internal static class Tables
{
    public static readonly OptionTable None = new();
    public static readonly OptionTable Time = new("pvqa", "fo", ["portability", "verbose", "quiet", "append"], ["format", "output"]);
    public static readonly OptionTable Timeout = new("v", "ks", ["preserve-status", "foreground", "verbose"], ["kill-after", "signal"]);
    public static readonly OptionTable Nice = new(WithValue: "n", LongWithValue: ["adjustment"]);
    public static readonly OptionTable Stdbuf = new(WithValue: "ioe", LongWithValue: ["input", "output", "error"]);
    public static readonly OptionTable Setsid = new("cfw", Long: ["ctty", "fork", "wait"]);
    public static readonly OptionTable Ionice = new("t", "cnpPu", ["ignore"], ["class", "classdata", "pid", "pgid", "uid"]);
    public static readonly OptionTable Command = new("pvV");
    public static readonly OptionTable Exec = new("cl", "a");
    public static readonly OptionTable Env = new("i0v", "uCS", ["ignore-environment", "null", "debug", "list-signal-handling"],
        ["unset", "chdir", "split-string"], ["block-signal", "default-signal", "ignore-signal"]);
    public static readonly OptionTable Xargs = new("0rtpxo", "aEdIPLns",
        ["null", "no-run-if-empty", "verbose", "interactive", "exit", "open-tty", "show-limits"],
        ["arg-file", "delimiter", "max-args", "max-procs", "max-chars", "process-slot-var", "eof"], ["replace", "max-lines"],
        OptionalAttached: "eil");
    public static readonly OptionTable Sudo = new("AbEeHhiKklnPSsVv", "aCcDgpRrTtUu",
        ["askpass", "background", "edit", "help", "set-home", "login", "remove-timestamp", "reset-timestamp", "list",
         "non-interactive", "preserve-groups", "shell", "stdin", "validate", "version", "bell"],
        ["close-from", "chdir", "group", "host", "prompt", "chroot", "role", "type", "command-timeout", "other-user", "user"],
        ["preserve-env"]);
    public static readonly OptionTable Doas = new("Lns", "aCu");
    public static readonly OptionTable Watch = new("bcegtxprw", "nq",
        ["beep", "color", "no-color", "errexit", "chgexit", "no-title", "exec", "precise", "no-wrap", "no-rerun"],
        ["interval", "equexit"], ["differences"], OptionalAttached: "d");
    // flock stops at its first operand (getopt "+"), the lock file.
    public static readonly OptionTable Flock = new("sxenouF", "wEc",
        ["shared", "exclusive", "unlock", "nonblock", "nb", "close", "no-fork", "verbose"],
        ["timeout", "wait", "conflict-exit-code", "command"]);
    public static readonly OptionTable Taskset = new("apc", Long: ["all-tasks", "pid", "cpu-list"]);
    public static readonly OptionTable Chrt = new("bdfiorRvae", "TPD",
        ["batch", "deadline", "fifo", "idle", "other", "rr", "reset-on-fork", "verbose", "all-tasks", "ext"],
        ["sched-runtime", "sched-period", "sched-deadline"]);
    public static readonly OptionTable Pkexec = new(Long: ["disable-internal-agent", "keep-cwd"], LongWithValue: ["user"]);
    public static readonly OptionTable Su = new("lmpfP", "cgGsw", ["login", "preserve-environment", "fast", "pty"],
        ["command", "group", "supp-group", "shell", "whitelist-environment", "session-command"], Permute: true);
}

internal static class Options
{
    /// <summary>
    /// Strict parsing for wrappers: an unknown option, or an option word computed at run time, is a refusal, because
    /// misreading a wrapper's option would misidentify the command it runs.
    /// </summary>
    public static bool Parse(List<Field> args, OptionTable table, out List<(string Name, Field? Value)> options, out int operand,
        out string? problem)
    {
        options = [];
        problem = null;
        operand = args.Count;
        var operands = new List<int>();
        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            if (arg.Dynamic)
            {
                if (arg.Text.StartsWith('-') || arg.Text.Length == 0 && operands.Count == 0)
                {
                    problem = $"option '{arg.Display}' is computed at run time";
                    return false;
                }

                if (!table.Permute)
                    break;
                operands.Add(i++);
                continue;
            }

            var text = arg.Text;
            if (text == "--")
            {
                i++;
                break;
            }

            if (text.Length < 2 || text[0] != '-')
            {
                if (!table.Permute)
                    break;
                operands.Add(i++);
                continue;
            }

            if (text.StartsWith("--", StringComparison.Ordinal))
            {
                var equals = text.IndexOf('=', StringComparison.Ordinal);
                var name = equals < 0 ? text[2..] : text[2..equals];
                if (table.IsLongWithValue(name))
                {
                    if (equals >= 0)
                        options.Add((name, Field.Literal(text[(equals + 1)..])));
                    else if (i + 1 < args.Count)
                        options.Add((name, args[++i]));
                    else
                    {
                        problem = $"option '{text}' needs a value";
                        return false;
                    }
                }
                else if (table.IsLongOptional(name))
                    options.Add((name, equals >= 0 ? Field.Literal(text[(equals + 1)..]) : null));
                else if (table.IsLongFlag(name) && equals < 0)
                    options.Add((name, null));
                else
                {
                    problem = $"unknown option '{text}'";
                    return false;
                }

                i++;
                continue;
            }

            for (var j = 1; j < text.Length; j++)
            {
                var flag = text[j];
                if (table.WithValue.Contains(flag))
                {
                    if (j + 1 < text.Length)
                        options.Add((flag.ToString(), Field.Literal(text[(j + 1)..])));
                    else if (i + 1 < args.Count)
                        options.Add((flag.ToString(), args[++i]));
                    else
                    {
                        problem = $"option '-{flag}' needs a value";
                        return false;
                    }

                    break;
                }

                if (table.OptionalAttached.Contains(flag))
                {
                    options.Add((flag.ToString(), j + 1 < text.Length ? Field.Literal(text[(j + 1)..]) : null));
                    break;
                }

                if (!table.Flags.Contains(flag))
                {
                    problem = $"unknown option '-{flag}'";
                    return false;
                }

                options.Add((flag.ToString(), null));
            }

            i++;
        }

        if (table.Permute)
        {
            // Permuted operands are moved to the end, in order, as getopt does.
            var rest = args.Skip(i).ToList();
            var ordered = operands.Select(index => args[index]).Concat(rest).ToList();
            args.Clear();
            args.AddRange(ordered);
            operand = 0;
            return true;
        }

        operand = i;
        return true;
    }

    // Long options the verb descriptions test for by name, for expanding abbreviations in Scan.
    private static readonly string[] SignificantLongNames =
    [
        "archive", "chdir", "checkpoint-action", "command", "compress-program", "delete", "dereference-recursive", "directories", "directory", "exec",
        "expression", "file", "from-file", "in-place", "info-script", "keep", "list", "log-file", "lua-exec", "mirror", "modify", "move",
        "output", "output-dir", "output-directory", "pager", "pgid", "pid", "pre", "recurse", "recurse-paths", "recursive",
        "reference", "regexp", "remove", "remove-source-files", "replace", "revert", "rsh", "rsync-path", "sandbox", "scp", "set",
        "sh-exec", "split-string", "stdout", "target-directory", "test", "to-command", "to-stdout", "uid", "unzip-command",
        "use-compress-program",
    ];

    /// <summary>
    /// Tolerant, permuting parsing for ordinary programs: unknown options count as flags, values are taken only for the
    /// options listed, and everything else is an operand. Dynamic words are operands (their targets are unresolved).
    /// </summary>
    public static (List<(string Name, Field? Value)> Options, List<Field> Operands) Scan(List<Field> args, OptionTable table)
    {
        var options = new List<(string, Field?)>();
        var operands = new List<Field>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Dynamic || arg.Text.Length < 2 || arg.Text[0] != '-')
            {
                operands.Add(arg);
                continue;
            }

            var text = arg.Text;
            if (text == "--")
            {
                operands.AddRange(args.Skip(i + 1));
                break;
            }

            if (text.StartsWith("--", StringComparison.Ordinal))
            {
                var equals = text.IndexOf('=', StringComparison.Ordinal);
                var name = equals < 0 ? text[2..] : text[2..equals];
                Field? value = null;
                if (equals >= 0)
                    value = Field.Literal(text[(equals + 1)..]);
                else if (table.IsLongWithValue(name) && i + 1 < args.Count)
                    value = args[++i];
                options.Add((name, value));
                // getopt_long accepts any unambiguous prefix ("rm --rec" is "rm --recursive"). Each long name the prefix could
                // stand for is recorded too, so a check for "recursive" sees it; a prefix the program finds ambiguous fails
                // there, so reading it every way errs on the safe side. Its value, if any, stays an operand.
                if (name.Length > 0 && !table.IsLongFlag(name) && !table.IsLongWithValue(name) && !table.IsLongOptional(name))
                    foreach (var full in SignificantLongNames.Concat(table.Long ?? []).Concat(table.LongWithValue ?? []).Concat(table.LongOptional ?? []))
                        if (full.Length > name.Length && full.StartsWith(name, StringComparison.Ordinal))
                            options.Add((full, value));
                continue;
            }

            for (var j = 1; j < text.Length; j++)
            {
                var flag = text[j];
                if (table.WithValue.Contains(flag))
                {
                    if (j + 1 < text.Length)
                        options.Add((flag.ToString(), Field.Literal(text[(j + 1)..])));
                    else if (i + 1 < args.Count)
                        options.Add((flag.ToString(), args[++i]));
                    break;
                }

                options.Add((flag.ToString(), null));
            }
        }

        return (options, operands);
    }
}

/// <summary>Accumulates one command's effects and targets, resolving paths against the scope's working directories.</summary>
internal sealed class CommandFacts(ShellAnalyzer analyzer, string verb, Scope scope)
{
    // Pseudo-devices that are not storage: writing or reading them changes nothing.
    private static readonly ImmutableHashSet<string> InertDevices = ImmutableHashSet.Create(StringComparer.Ordinal,
        "/dev/null", "/dev/zero", "/dev/full", "/dev/random", "/dev/urandom", "/dev/stdin", "/dev/stdout", "/dev/stderr", "/dev/tty");

    private readonly List<ActionTarget> _targets = [];
    private readonly List<string> _unresolved = [];

    public ActionEffects Effects { get; set; }

    public string? Subcommand { get; set; }

    public string Verb => verb;

    public bool HasTargets => _targets.Count > 0;

    public ApprovalEnvironment Env => analyzer.Environment;

    public void Unresolved(string reason) => _unresolved.Add(reason);

    /// <summary>Code this command runs that Corvid does not parse; the hardline backstop scans it.</summary>
    public void OpaqueScript(string text) => analyzer.Opaque(text);

    public void Path(Field field, TargetAccess access, bool recursive = false)
    {
        if (field.Dynamic)
        {
            var prefix = field.Text;
            if (prefix.StartsWith('/'))
                AddPath(new ActionTarget(TargetKind.Path, PathMath.Normalize(DirectoryOf(prefix)), access, PathLocation.Unresolved,
                    recursive, Dynamic: true, Written: field.Display));
            else
                foreach (var cwd in scope.Cwds)
                    AddPath(new ActionTarget(TargetKind.Path,
                        cwd is null || prefix.Length == 0 ? "" : PathMath.Normalize(DirectoryOf(System.IO.Path.Combine(cwd, prefix))),
                        access, PathLocation.Unresolved, recursive, Dynamic: true, Written: field.Display));
            return;
        }

        var text = field.Text;
        if (text.Length == 0)
            return;
        ImmutableArray<string?> bases = text.StartsWith('/') ? [""] : scope.Cwds;
        foreach (var cwd in bases)
        {
            if (cwd is null)
            {
                AddPath(new ActionTarget(TargetKind.Path, text, access, PathLocation.Unresolved, recursive, field.Glob,
                    Written: field.Display));
                continue;
            }

            var full = cwd.Length == 0 ? text : System.IO.Path.Combine(cwd, text);
            if (field.Glob && full.AsSpan().IndexOfAny("*?[") >= 0)
            {
                var (directory, rest) = SplitPattern(full);
                var normalized = PathMath.Normalize(directory);
                AddPath(new ActionTarget(TargetKind.Path, normalized == "/" ? "/" + rest : normalized + "/" + rest, access,
                    LocatePattern(normalized, rest), recursive, Pattern: true, Written: field.Display));
            }
            else
            {
                var normalized = PathMath.Normalize(full);
                if (InertDevices.Contains(normalized) || normalized.StartsWith("/dev/fd/", StringComparison.Ordinal)
                    || normalized.StartsWith("/proc/self/fd/", StringComparison.Ordinal))
                    continue;
                AddPath(new ActionTarget(TargetKind.Path, normalized, access, Locate(normalized), recursive, Written: field.Display));
            }
        }
    }

    /// <summary>Possible directories a <c>cd</c>-like argument names; null where it cannot be resolved.</summary>
    public ImmutableArray<string?> Directories(Field field)
    {
        if (field.Dynamic || field.Glob)
            return [null];
        if (field.Text.StartsWith('/'))
            return [PathMath.Normalize(field.Text)];
        return [.. scope.Cwds.Select(cwd => cwd is null ? null : PathMath.Normalize(System.IO.Path.Combine(cwd, field.Text))).Distinct()];
    }

    public void Process(Field pid)
    {
        Effects |= ActionEffects.Signal;
        Add(new ActionTarget(TargetKind.Process, pid.Dynamic ? "" : pid.Text, TargetAccess.Signal, Dynamic: pid.Dynamic, Written: pid.Display));
    }

    public void ProcessName(Field name)
    {
        Effects |= ActionEffects.Signal;
        Add(new ActionTarget(TargetKind.ProcessName, name.Dynamic ? "" : name.Text, TargetAccess.Signal, Dynamic: name.Dynamic,
            Written: name.Display));
    }

    public void Host(string host, string written, bool dynamic = false)
    {
        Effects |= ActionEffects.Network;
        Add(new ActionTarget(TargetKind.Host, host.ToLowerInvariant(), TargetAccess.Connect, Dynamic: dynamic, Written: written));
    }

    /// <summary>A URL operand: its host, or a path for <c>file://</c>.</summary>
    public void Url(Field field)
    {
        Effects |= ActionEffects.Network;
        if (field.Dynamic)
        {
            Add(new ActionTarget(TargetKind.Url, field.Text, TargetAccess.Connect, Dynamic: true, Written: field.Display));
            return;
        }

        if (Uri.TryCreate(field.Text, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            Path(Field.Literal(uri.LocalPath), TargetAccess.Read);
            return;
        }

        if (Uri.TryCreate(field.Text, UriKind.Absolute, out uri) && uri.Host.Length > 0)
        {
            Add(new ActionTarget(TargetKind.Url, field.Text, TargetAccess.Connect, Written: field.Display));
            Host(uri.Host, field.Display);
        }
        else
            Host(field.Text, field.Display);
    }

    public void AddRedirects(ImmutableArray<(Field Target, bool Writes)> redirects)
    {
        if (redirects.IsDefault)
            return;
        foreach (var (target, writes) in redirects)
            Path(target, writes ? TargetAccess.Write : TargetAccess.Read);
    }

    public AnalyzedCommand Build(string text, IReadOnlyList<Field>? arguments)
    {
        var effects = Effects;
        if (scope.Elevated)
            effects |= ActionEffects.Elevated;
        IEnumerable<string> unresolved = _unresolved;
        if (scope.Opaque is { } opaque)
        {
            effects |= ActionEffects.Execute;
            unresolved = _unresolved.Prepend(opaque);
        }

        if (effects == ActionEffects.None)
            effects = ActionEffects.Read;
        return new AnalyzedCommand(verb, Subcommand, effects, [.. _targets.Distinct()], [.. unresolved.Distinct()], scope.Via,
            scope.Substitution, text, arguments is null ? [] : [.. arguments.Select(field => field.Dynamic ? field.Display : field.Text)]);
    }

    private void Add(ActionTarget target) => _targets.Add(target);

    // Effects follow the targets actually recorded, so a redirection to /dev/null changes nothing.
    private void AddPath(ActionTarget target)
    {
        Effects |= target.Access switch
        {
            TargetAccess.Read => ActionEffects.Read,
            TargetAccess.Write => ActionEffects.Write,
            TargetAccess.Delete => ActionEffects.Delete,
            TargetAccess.Execute => ActionEffects.Execute,
            _ => ActionEffects.None,
        };
        if (target.Recursive && target.Access is TargetAccess.Write or TargetAccess.Delete)
            Effects |= ActionEffects.Recursive;
        _targets.Add(target);
    }

    private PathLocation Locate(string path) =>
        path == Env.Workspace ? PathLocation.WorkspaceRoot
        : PathMath.IsUnder(path, Env.Workspace) ? PathLocation.Inside
        : PathLocation.Outside;

    // A glob's matches lie below its static directory. One component below it, every match is a child: inside when the
    // directory is inside, otherwise outside (a child of an ancestor of the workspace is that ancestor's own entry). Deeper
    // patterns under an ancestor may match on both sides.
    private PathLocation LocatePattern(string directory, string rest)
    {
        if (PathMath.Components(rest).Any(component => component == ".."))
            return PathLocation.Unresolved;
        if (PathMath.IsAtOrUnder(directory, Env.Workspace))
            return PathLocation.Inside;
        if (!rest.Contains('/'))
            return PathLocation.Outside;
        return PathMath.IsUnder(Env.Workspace, directory) ? PathLocation.Unresolved : PathLocation.Outside;
    }

    private static (string Directory, string Remainder) SplitPattern(string path)
    {
        var glob = path.AsSpan().IndexOfAny("*?[");
        var slash = path.LastIndexOf('/', glob);
        return slash <= 0 ? ("/", path[(slash + 1)..]) : (path[..slash], path[(slash + 1)..]);
    }

    private static string DirectoryOf(string prefix)
    {
        var slash = prefix.LastIndexOf('/');
        return slash <= 0 ? "/" : prefix[..slash];
    }
}

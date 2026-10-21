using System.Collections.Immutable;
using System.Text;

namespace Corvid.Approvals.Shell;

/// <summary>A word after expansion. <see cref="Text"/> is exact when static; for a dynamic field it is the static prefix.</summary>
internal sealed record Field(string Text, bool Dynamic, bool Glob, string Display)
{
    public static Field Literal(string text) => new(text, false, false, text);

    public static Field Unknown(string display) => new("", true, false, display);

    /// <summary>A static option word. A dynamic word could also be an option, which callers must allow for.</summary>
    public bool IsOption => !Dynamic && Text.Length > 1 && Text[0] == '-';
}

internal enum StdinKind
{
    Inherited,
    /// <summary>A pipe, a file, or a here-document with expansions: content unknown.</summary>
    Unknown,
    /// <summary>A here-string or here-document whose content is static.</summary>
    Literal,
}

internal sealed record Stdin(StdinKind Kind, string? Text = null)
{
    public static readonly Stdin Inherited = new(StdinKind.Inherited);
    public static readonly Stdin Unknown = new(StdinKind.Unknown);
}

/// <param name="Cwds">Possible working directories; null means unknown.</param>
/// <param name="Variables">Known values; null means the variable is set to something unknown.</param>
/// <param name="Opaque">
/// Why commands may not be what they say: earlier code Corvid cannot see (<c>source</c>), or a variable that changes which
/// program runs or loads code into it (<c>PATH</c>, <c>LD_PRELOAD</c>, <c>BASH_ENV</c>). Null when nothing did.
/// </param>
internal sealed record Scope(
    ImmutableArray<string?> Cwds,
    ImmutableDictionary<string, string?> Variables,
    ImmutableHashSet<string> Exported,
    bool Elevated,
    ImmutableArray<string> Via,
    bool Substitution,
    string? Opaque);

/// <summary>What a command changes in the shell that runs it, when it runs in that shell (not a pipeline or background).</summary>
internal sealed record StateChange(
    ImmutableArray<string?>? Cwds = null,
    ImmutableDictionary<string, string?>? Assignments = null,
    ImmutableArray<string> Exports = default,
    string? Opaque = null);

/// <summary>Redirections and stdin that travel with a command through its wrappers.</summary>
internal sealed record Extras(Stdin Stdin, ImmutableArray<(Field Target, bool Writes)> Redirects)
{
    public static readonly Extras None = new(Stdin.Inherited, []);
}

/// <summary>
/// Walks a parsed script: expands words statically, follows <c>cd</c> and assignments, unwraps wrappers recursively,
/// analyses substitutions and nested shells, and records every command that would run (plan §10.2 steps 1 and 2).
/// </summary>
internal sealed partial class ShellAnalyzer
{
    private const int MaxCwds = 8;
    private const int MaxNesting = 8;
    private const int MaxCommands = 256;

    private static readonly ImmutableHashSet<string> SystemBinaries = ImmutableHashSet.Create(StringComparer.Ordinal,
        "/bin", "/usr/bin", "/sbin", "/usr/sbin", "/usr/local/bin", "/usr/local/sbin");

    private static readonly ImmutableHashSet<string> Shells = ImmutableHashSet.Create(StringComparer.Ordinal, "sh", "bash", "dash");

    private readonly ApprovalEnvironment _env;
    private readonly List<AnalyzedCommand> _commands = [];
    private readonly List<string> _opaqueScripts = [];
    private List<Field>? _arguments;

    private ShellAnalyzer(ApprovalEnvironment env)
    {
        _env = env;
    }

    public ApprovalEnvironment Environment => _env;

    public static ActionAnalysis Analyze(string command, ApprovalEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(env);
        var analyzer = new ShellAnalyzer(env);
        switch (ShellParser.Parse(command))
        {
            case ShellUnsupported unsupported:
                return new ActionAnalysis("shell", command, [], $"{unsupported.Reason} (at {unsupported.Position})")
                    { OpaqueScripts = [command] };
            case ShellParsed parsed:
                try
                {
                    analyzer.Script(parsed.Script, analyzer.RootScope(), 0);
                }
                catch (AnalysisUnsupported unsupported)
                {
                    return new ActionAnalysis("shell", command, [], unsupported.Message) { OpaqueScripts = [command] };
                }

                return new ActionAnalysis("shell", command, [.. analyzer._commands], null) { OpaqueScripts = [.. analyzer._opaqueScripts] };
            default:
                throw new InvalidOperationException("Unknown parse result.");
        }
    }

    /// <summary>Thrown for constructs that change how every later command must be read (aliases, traps).</summary>
    private sealed class AnalysisUnsupported(string message) : Exception(message);

    private Scope RootScope() => new(
        [_env.Workspace],
        _env.Variables.ToImmutableDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal),
        ImmutableHashSet.Create<string>(StringComparer.Ordinal),
        Elevated: false,
        Via: [],
        Substitution: false,
        Opaque: null);

    private void Script(ShellScript script, Scope scope, int nesting)
    {
        var current = scope.Cwds;
        var all = scope.Cwds;
        var variables = scope.Variables;
        var exported = scope.Exported;
        var opaque = scope.Opaque;
        var previousBackground = false;
        foreach (var item in script.Items)
        {
            // After ";" or "||" (or a background item) the previous cd may or may not have happened.
            if (item.Connector is ShellConnector.Sequence or ShellConnector.Or || previousBackground)
                current = all;
            previousBackground = item.Background;
            var itemScope = scope with { Cwds = current, Variables = variables, Exported = exported, Opaque = opaque };
            var unconditional = item.Connector is ShellConnector.None or ShellConnector.Sequence;
            var persistent = item.Pipeline.Commands.Length == 1 && !item.Background;
            for (var i = 0; i < item.Pipeline.Commands.Length; i++)
            {
                var change = Command(item.Pipeline.Commands[i], itemScope, i == 0 ? Stdin.Inherited : Stdin.Unknown, nesting);
                if (!persistent || change is null)
                    continue;
                if (change.Cwds is { } cwds)
                {
                    current = cwds;
                    all = Union(all, cwds);
                }

                if (change.Assignments is { } assignments)
                    foreach (var (name, value) in assignments)
                        variables = variables.SetItem(name,
                            unconditional || variables.TryGetValue(name, out var old) && old == value ? value : null);
                if (!change.Exports.IsDefaultOrEmpty)
                    exported = exported.Union(change.Exports);
                opaque ??= change.Opaque;
                if (change.Assignments is { } changed && changed.Keys.FirstOrDefault(IsCodeVariable) is { } loader)
                    opaque ??= $"follows an assignment to {loader}, which can change what runs";
            }
        }
    }

    // Variables that choose the program, load code into it, or run code on its behalf. A command that sets one is opaque.
    private static readonly ImmutableHashSet<string> CodeVariables = ImmutableHashSet.Create(StringComparer.Ordinal,
        "PATH", "LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT", "BASH_ENV", "ENV", "CDPATH", "IFS", "PROMPT_COMMAND", "SHELLOPTS",
        "BASHOPTS", "PAGER", "GIT_PAGER", "MANPAGER", "LESSOPEN", "LESSCLOSE", "EDITOR", "VISUAL", "GIT_SSH", "GIT_SSH_COMMAND",
        "GIT_EXTERNAL_DIFF", "GIT_ASKPASS", "SSH_ASKPASS", "GIT_EXEC_PATH", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM",
        "GIT_CONFIG_COUNT", "PYTHONSTARTUP", "PYTHONPATH", "PYTHONHOME", "NODE_OPTIONS", "NODE_PATH", "PERL5OPT", "PERL5LIB",
        "RUBYOPT", "RUBYLIB", "DOTNET_STARTUP_HOOKS", "JAVA_TOOL_OPTIONS");

    private static bool IsCodeVariable(string name) =>
        CodeVariables.Contains(name) || name.StartsWith("GIT_CONFIG_", StringComparison.Ordinal) || name.StartsWith("BASH_FUNC_", StringComparison.Ordinal);

    private static ImmutableArray<string?> Union(ImmutableArray<string?> left, ImmutableArray<string?> right)
    {
        var union = left.Concat(right).Distinct().ToImmutableArray();
        return union.Length > MaxCwds ? [null] : union;
    }

    private StateChange? Command(ShellCommand command, Scope scope, Stdin stdin, int nesting)
    {
        var substitutions = new List<ShellScript>();
        var assigned = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
        foreach (var assignment in command.Assignments)
            assigned[assignment.Name] = assignment.Append ? null : Joined(assignment.Value, scope, substitutions);

        var fields = command.Words.SelectMany(word => Expand(word, scope, substitutions)).ToList();
        var redirects = ImmutableArray.CreateBuilder<(Field, bool)>();
        foreach (var redirect in command.Redirects)
        {
            if (redirect.NamesFile)
            {
                foreach (var target in Expand(redirect.Target, scope, substitutions))
                    redirects.Add((target, redirect.Writes));
                if (redirect.Kind == RedirectKind.Input)
                    stdin = Stdin.Unknown;
            }
            else if (redirect.Kind is RedirectKind.HereString or RedirectKind.HereDocument)
            {
                stdin = Joined(redirect.Target, scope, substitutions) is { } text ? new Stdin(StdinKind.Literal, text) : Stdin.Unknown;
            }
        }

        // Substitutions run first, each in a subshell of this one.
        foreach (var substitution in substitutions)
            Nested(() => Script(substitution, scope with { Substitution = true }, nesting + 1), nesting, "substitution");

        if (fields.Count == 0)
        {
            if (redirects.Count > 0)
            {
                var facts = NewFacts("(redirection)", scope);
                facts.AddRedirects(redirects.ToImmutable());
                var outer = _arguments;
                _arguments = null;
                Emit(facts, Render(command));
                _arguments = outer;
            }

            return new StateChange(Assignments: assigned.ToImmutable());
        }

        // Prefix assignments are the program's environment; they do not change how its own words expanded.
        var programScope = scope with
        {
            Variables = scope.Variables.SetItems(assigned),
            Exported = scope.Exported.Union(assigned.Keys),
            Opaque = scope.Opaque ?? (assigned.Keys.FirstOrDefault(IsCodeVariable) is { } loader
                ? $"runs with {loader} set, which can change what runs" : null),
        };
        return Invocation(fields, programScope, new Extras(stdin, redirects.ToImmutable()), nesting, Render(command));
    }

    private static string Render(ShellCommand command) =>
        string.Join(' ', command.Assignments.Select(a => $"{a.Name}{(a.Append ? "+=" : "=")}{a.Value.Source}")
            .Concat(command.Words.Select(word => word.Source))
            .Concat(command.Redirects.Select(r => r.Kind == RedirectKind.HereDocument ? "<<…" : r.Kind == RedirectKind.Duplicate ? ">&" + r.Target.Source : (r.Writes ? ">" : "<") + r.Target.Source)));

    private void Nested(Action analyse, int nesting, string what)
    {
        if (nesting + 1 > MaxNesting)
            throw new AnalysisUnsupported($"{what} nested deeper than {MaxNesting}");
        analyse();
    }

    internal CommandFacts NewFacts(string verb, Scope scope) => new(this, verb, scope);

    internal void Emit(CommandFacts facts, string text)
    {
        if (_commands.Count >= MaxCommands)
            throw new AnalysisUnsupported($"more than {MaxCommands} commands");
        _commands.Add(facts.Build(text, _arguments));
    }

    /// <summary>Records text Corvid runs but cannot analyse (a script outside the subset, an interpreter's inline code).</summary>
    internal void Opaque(string text) => _opaqueScripts.Add(text);

    /// <summary>The program and its arguments after expansion: unwrap wrappers, follow builtins, then describe the verb.</summary>
    private StateChange? Invocation(List<Field> fields, Scope scope, Extras extras, int nesting, string text)
    {
        var outer = _arguments;
        _arguments = fields;
        try
        {
            return Unwrap(fields, scope, extras, nesting, text);
        }
        finally
        {
            _arguments = outer;
        }
    }

    private StateChange? Unwrap(List<Field> fields, Scope scope, Extras extras, int nesting, string text)
    {
        var program = fields[0];
        var args = fields.Skip(1).ToList();
        if (program.Dynamic || program.Glob)
        {
            var facts = NewFacts("?", scope);
            facts.Effects |= ActionEffects.Execute;
            facts.Unresolved(program.Dynamic ? $"the program '{program.Display}' is computed at run time" : "the program name is a pattern");
            facts.AddRedirects(extras.Redirects);
            Emit(facts, text);
            return null;
        }

        var verb = program.Text;
        if (verb.Contains('/'))
        {
            // /usr/bin/rm is rm; ./rm or ~/bin/rm is a program Corvid cannot see. Its arguments are still read as the
            // program it is named after, so "./rm -rf /" meets the hardline list as well as asking.
            var directory = Path.GetDirectoryName(verb);
            if (directory is not null && SystemBinaries.Contains(directory))
                verb = Path.GetFileName(verb);
            else
            {
                var facts = NewFacts(verb, scope);
                Verbs.Describe(facts, Path.GetFileName(verb), args);
                facts.Effects |= ActionEffects.Execute;
                facts.Path(program, TargetAccess.Execute);
                facts.AddRedirects(extras.Redirects);
                Emit(facts, text);
                return null;
            }
        }

        // After "source" or with PATH-like variables set, a name may not be the program it names. Every command is then
        // unresolved (CommandFacts.Build), but it is still described as named, so the hardline list sees its targets.
        if (scope.Opaque is not null && verb is "cd" or "pushd" or "popd")
        {
            ChangeDirectory(verb, args, scope, extras, text);
            return new StateChange(Cwds: [null]);
        }

        switch (verb)
        {
            case "cd" or "pushd" or "popd":
                return ChangeDirectory(verb, args, scope, extras, text);
            case "export" or "declare" or "typeset" or "local" or "readonly" or "unset" or "read" or "mapfile" or "readarray"
                or "let" or "getopts":
                return Declaration(verb, args, scope, extras, text);
            case "alias" or "unalias" or "trap" or "enable" or "hash" or "shopt" when verb != "shopt" || args.Any(a => a.Dynamic || a.Text == "expand_aliases"):
                throw new AnalysisUnsupported($"'{verb}' changes how later commands run");
            case "source" or ".":
            {
                var facts = NewFacts(verb, scope);
                facts.Effects |= ActionEffects.Execute;
                if (args.Count > 0)
                    facts.Path(args[0], TargetAccess.Execute);
                facts.AddRedirects(extras.Redirects);
                Emit(facts, text);
                return new StateChange(Opaque: "follows 'source', which may have redefined commands");
            }
            case "eval":
                return Eval(args, scope, extras, nesting, text);
            case "printf":
            {
                var facts = NewFacts(verb, scope);
                facts.Effects |= ActionEffects.Read;
                facts.AddRedirects(extras.Redirects);
                Emit(facts, text);
                var target = args.FindIndex(arg => !arg.Dynamic && arg.Text == "-v");
                return target >= 0 && target + 1 < args.Count
                    ? new StateChange(Assignments: ImmutableDictionary<string, string?>.Empty.Add(args[target + 1].Text, null))
                    : null;
            }
            case "builtin":
                return args.Count == 0 ? null : Invocation(args, Via(scope, "builtin"), extras, nesting, text);
            case "command":
                return Command(args, scope, extras, nesting, text);
            case "exec":
                return Exec(args, scope, extras, nesting, text);
            case "time":
                return Wrapper(verb, args, Tables.Time, scope, extras, nesting, text, operandsBeforeCommand: 0,
                    (facts, options) =>
                    {
                        foreach (var (name, value) in options)
                            if (name is "o" or "output" && value is not null)
                                facts.Path(value, TargetAccess.Write);
                    });
            case "timeout":
                return Wrapper(verb, args, Tables.Timeout, scope, extras, nesting, text, operandsBeforeCommand: 1);
            case "nice":
                return Nice(args, scope, extras, nesting, text);
            case "nohup":
                return Wrapper(verb, args, Tables.None, scope, extras, nesting, text, operandsBeforeCommand: 0,
                    (facts, _) => facts.Path(Field.Literal("nohup.out"), TargetAccess.Write));
            case "stdbuf":
                return Wrapper(verb, args, Tables.Stdbuf, scope, extras, nesting, text, operandsBeforeCommand: 0);
            case "setsid":
                return Wrapper(verb, args, Tables.Setsid, scope, extras, nesting, text, operandsBeforeCommand: 0);
            case "ionice":
                return Ionice(args, scope, extras, nesting, text);
            case "env":
                return Env(args, scope, extras, nesting, text);
            case "xargs":
                return Xargs(args, scope, extras, nesting, text);
            case "sudo":
                return Sudo(args, scope, extras, nesting, text);
            case "sudoedit":
                return Sudo([Field.Literal("-e"), .. args], scope, extras, nesting, text);
            case "doas":
                return Doas(args, scope, extras, nesting, text);
            case "su":
                return Su(args, scope, extras, nesting, text);
            case "pkexec":
                return Wrapper(verb, args, Tables.Pkexec, scope with { Elevated = true }, extras, nesting, text, operandsBeforeCommand: 0,
                    emptyReason: "pkexec without a program starts a root shell");
            case "watch":
                return Watch(args, scope, extras, nesting, text);
            case "flock":
                return Flock(args, scope, extras, nesting, text);
            case "taskset" or "chrt":
                return Scheduler(verb, args, scope, extras, nesting, text);
            case "busybox":
                return Busybox(args, scope, extras, nesting, text);
            case "chroot":
            {
                var facts = NewFacts(verb, Elevate(scope, verb));
                facts.Effects |= ActionEffects.Execute | ActionEffects.Elevated;
                facts.Unresolved("chroot changes the root directory, so no path can be resolved");
                Emit(facts, text);
                return null;
            }
            case "find":
                Find(args, scope, extras, nesting, text);
                return null;
            // Launchers (Launchers.cs): tracers, namespaces and sandboxes, the service manager, shell strings.
            case "strace":
                return Strace(args, scope, extras, nesting, text);
            case "ltrace":
                return Ltrace(args, scope, extras, nesting, text);
            case "valgrind":
                return Valgrind(args, scope, extras, nesting, text);
            case "perf":
                return Perf(args, scope, extras, nesting, text);
            case "nsenter":
                return Nsenter(args, scope, extras, nesting, text);
            case "unshare":
                return Unshare(args, scope, extras, nesting, text);
            case "firejail":
                return Firejail(args, scope, extras, nesting, text);
            case "bwrap":
                return Bwrap(args, scope, extras, nesting, text);
            case "systemd-run":
                return SystemdRun(args, scope, extras, nesting, text);
            case "script":
                return ScriptCommand(args, scope, extras, nesting, text);
            case "parallel":
                return ParallelCommand(args, scope, extras, nesting, text);
        }

        if (Shells.Contains(verb))
        {
            Shell(verb, args, scope, extras, nesting, text);
            return null;
        }

        var described = NewFacts(verb, scope);
        Verbs.Describe(described, verb, args);
        described.AddRedirects(extras.Redirects);
        Emit(described, text);
        return null;
    }

    private static Scope Via(Scope scope, string wrapper) => scope with { Via = scope.Via.Add(wrapper) };

    private static Scope Elevate(Scope scope, string wrapper) => scope with { Via = scope.Via.Add(wrapper), Elevated = true };

    /// <summary>A failed wrapper parse: never guessed past, recorded as an unresolved command.</summary>
    private StateChange? Refuse(string verb, Scope scope, Extras extras, string text, string reason)
    {
        var facts = NewFacts(verb, scope);
        facts.Effects |= ActionEffects.Execute;
        facts.Unresolved(reason);
        facts.AddRedirects(extras.Redirects);
        Emit(facts, text);
        return null;
    }

    private StateChange? Wrapper(string verb, List<Field> args, OptionTable table, Scope scope, Extras extras, int nesting,
        string text, int operandsBeforeCommand, Action<CommandFacts, List<(string Name, Field? Value)>>? describe = null,
        string? emptyReason = null)
    {
        if (!Options.Parse(args, table, out var options, out var operand, out var problem))
            return Refuse(verb, scope, extras, text, $"{verb}: {problem}");
        var inner = args.Skip(operand + operandsBeforeCommand).ToList();
        if (describe is not null)
        {
            var facts = NewFacts(verb, scope);
            describe(facts, options);
            if (facts.HasTargets)
                Emit(facts, text);
        }

        if (inner.Count == 0)
            return emptyReason is null ? null : Refuse(verb, scope, extras, text, emptyReason);
        return Invocation(inner, Via(scope, verb), extras, nesting, text);
    }

    // watch joins its arguments with spaces and runs them with "sh -c", unless -x runs them directly.
    private StateChange? Watch(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        if (!Options.Parse(args, Tables.Watch, out var options, out var operand, out var problem))
            return Refuse("watch", scope, extras, text, $"watch: {problem}");
        var inner = args.Skip(operand).ToList();
        if (inner.Count == 0)
            return null;
        if (options.Any(option => option.Name is "x" or "exec"))
            return Invocation(inner, Via(scope, "watch"), extras, nesting, text);
        if (inner.Any(field => field.Dynamic))
            return Refuse("watch", scope, extras, text, "watch: the command is computed at run time");
        NestedScript("watch", string.Join(' ', inner.Select(field => field.Text)), ChildShell(Via(scope, "watch")), nesting, text,
            extras.Redirects);
        return null;
    }

    // flock [options] FILE COMMAND [ARGS], or FILE -c 'COMMAND' (run by sh -c), or a descriptor number and nothing else.
    private StateChange? Flock(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        if (!Options.Parse(args, Tables.Flock, out var options, out var operand, out var problem))
            return Refuse("flock", scope, extras, text, $"flock: {problem}");
        var operands = args.Skip(operand).ToList();
        if (operands.Count == 0)
            return Refuse("flock", scope, extras, text, "flock without a lock file");
        var lockFile = operands[0];
        var rest = operands.Skip(1).ToList();
        // "-c" may also follow the lock file, where getopt has already stopped.
        var command = options.LastOrDefault(option => option.Name is "c" or "command").Value;
        if (command is null && rest.Count >= 2 && !rest[0].Dynamic && rest[0].Text is "-c" or "--command")
        {
            command = rest[1];
            rest = [];
        }

        var facts = NewFacts("flock", scope);
        if (lockFile.Dynamic || !lockFile.Text.All(char.IsAsciiDigit))
            facts.Path(lockFile, TargetAccess.Write);
        facts.AddRedirects(extras.Redirects);
        Emit(facts, text);
        if (command is not null)
        {
            if (command.Dynamic)
                return Refuse("flock", scope, Extras.None, text, "flock -c: the command is computed at run time");
            NestedScript("flock", command.Text, ChildShell(Via(scope, "flock -c")), nesting, text);
            return null;
        }

        return rest.Count == 0 ? null : Invocation(rest, Via(scope, "flock"), Extras.None, nesting, text);
    }

    // taskset MASK COMMAND and chrt PRIORITY COMMAND run a command; with -p they change a running process instead.
    private StateChange? Scheduler(string verb, List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        var table = verb == "taskset" ? Tables.Taskset : Tables.Chrt;
        if (!Options.Parse(args, table, out var options, out var operand, out var problem))
            return Refuse(verb, scope, extras, text, $"{verb}: {problem}");
        var operands = args.Skip(operand).ToList();
        if (options.Any(option => option.Name is "p" or "pid"))
        {
            var facts = NewFacts(verb, scope);
            facts.Effects |= ActionEffects.System;
            if (operands.Count > 0)
                facts.Process(operands[^1]);
            facts.AddRedirects(extras.Redirects);
            Emit(facts, text);
            return null;
        }

        var inner = operands.Skip(1).ToList();
        if (inner.Count == 0)
        {
            var facts = NewFacts(verb, scope);
            facts.AddRedirects(extras.Redirects);
            Emit(facts, text);
            return null;
        }

        return Invocation(inner, Via(scope, verb), extras, nesting, text);
    }

    // busybox APPLET ARGS is the applet; options other than listing (--install writes links) are refused.
    private StateChange? Busybox(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        if (args.Count == 0 || !args[0].Dynamic && args[0].Text is "--list" or "--list-full" or "--help")
        {
            var facts = NewFacts("busybox", scope);
            facts.AddRedirects(extras.Redirects);
            Emit(facts, text);
            return null;
        }

        if (args[0].Dynamic || args[0].Text.StartsWith('-'))
            return Refuse("busybox", scope, extras, text, $"busybox: '{args[0].Display}' is not an applet");
        return Invocation(args, Via(scope, "busybox"), extras, nesting, text);
    }

    private StateChange? Nice(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        // "nice -10 cmd" and "nice --10 cmd" are the legacy adjustment forms.
        if (args.Count > 0 && !args[0].Dynamic && args[0].Text.StartsWith('-')
            && args[0].Text.TrimStart('-') is { Length: > 0 } adjustment && adjustment.All(char.IsAsciiDigit)
            && args[0].Text.Length - adjustment.Length <= 2)
            args = args.Skip(1).ToList();
        return Wrapper("nice", args, Tables.Nice, scope, extras, nesting, text, operandsBeforeCommand: 0);
    }

    private StateChange? Ionice(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        if (!Options.Parse(args, Tables.Ionice, out var options, out var operand, out var problem))
            return Refuse("ionice", scope, extras, text, $"ionice: {problem}");
        if (options.Any(option => option.Name is "p" or "P" or "u" or "pid" or "pgid" or "uid"))
        {
            var facts = NewFacts("ionice", scope);
            facts.Effects |= ActionEffects.System;
            foreach (var pid in args.Skip(operand))
                facts.Process(pid);
            Emit(facts, text);
            return null;
        }

        var inner = args.Skip(operand).ToList();
        return inner.Count == 0 ? null : Invocation(inner, Via(scope, "ionice"), extras, nesting, text);
    }

    private StateChange? Command(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        if (!Options.Parse(args, Tables.Command, out var options, out var operand, out var problem))
            return Refuse("command", scope, extras, text, $"command: {problem}");
        if (options.Any(option => option.Name is "v" or "V"))
        {
            var facts = NewFacts("command", scope);
            facts.Effects |= ActionEffects.Read;
            facts.AddRedirects(extras.Redirects);
            Emit(facts, text);
            return null;
        }

        var inner = args.Skip(operand).ToList();
        return inner.Count == 0 ? null : Invocation(inner, Via(scope, "command"), extras, nesting, text);
    }

    private StateChange? Exec(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        if (!Options.Parse(args, Tables.Exec, out _, out var operand, out var problem))
            return Refuse("exec", scope, extras, text, $"exec: {problem}");
        var inner = args.Skip(operand).ToList();
        if (inner.Count > 0)
            return Invocation(inner, Via(scope, "exec"), extras, nesting, text);
        if (extras.Redirects.Length > 0)
        {
            var facts = NewFacts("exec", scope);
            facts.AddRedirects(extras.Redirects);
            Emit(facts, text);
        }

        return null;
    }

    private StateChange? Env(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        if (!Options.Parse(args, Tables.Env, out var options, out var operand, out var problem))
            return Refuse("env", scope, extras, text, $"env: {problem}");
        if (options.Any(option => option.Name is "S" or "split-string"))
            return Refuse("env", scope, extras, text, "env -S splits its argument with its own rules");
        var inner = args.Skip(operand).ToList();
        if (inner.Count > 0 && !inner[0].Dynamic && inner[0].Text == "-")
            inner.RemoveAt(0);
        var variables = scope.Variables;
        var names = new List<string>();
        while (inner.Count > 0 && inner[0].Text.IndexOf('=', StringComparison.Ordinal) is > 0 and var equals
               && ShellParserNames.IsName(inner[0].Text[..equals]))
        {
            var name = inner[0].Text[..equals];
            variables = variables.SetItem(name, inner[0].Dynamic ? null : inner[0].Text[(equals + 1)..]);
            names.Add(name);
            inner.RemoveAt(0);
        }

        if (inner.Count > 0 && inner[0].Dynamic)
            return Refuse("env", scope, extras, text, $"env: the program '{inner[0].Display}' is computed at run time");
        var innerScope = Via(scope, "env") with
        {
            Variables = variables,
            Exported = scope.Exported.Union(names),
            Opaque = scope.Opaque ?? (names.FirstOrDefault(IsCodeVariable) is { } loader ? $"runs with {loader} set, which can change what runs" : null),
        };
        if (options.FirstOrDefault(option => option.Name is "C" or "chdir").Value is { } directory)
            innerScope = innerScope with { Cwds = directory.Dynamic ? [null] : NewFacts("env", scope).Directories(directory) };
        if (inner.Count == 0)
        {
            var facts = NewFacts("env", scope);
            facts.Effects |= ActionEffects.Read;
            facts.AddRedirects(extras.Redirects);
            Emit(facts, text);
            return null;
        }

        return Invocation(inner, innerScope, extras, nesting, text);
    }

    private StateChange? Xargs(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        if (!Options.Parse(args, Tables.Xargs, out var options, out var operand, out var problem))
            return Refuse("xargs", scope, extras, text, $"xargs: {problem}");
        foreach (var (name, value) in options)
            if (name is "a" or "arg-file" && value is not null)
            {
                var facts = NewFacts("xargs", scope);
                facts.Path(value, TargetAccess.Read);
                Emit(facts, text);
            }

        var inner = args.Skip(operand).ToList();
        if (inner.Count == 0)
            inner.Add(Field.Literal("echo"));
        var input = Field.Unknown("<xargs input>");
        var replace = options.LastOrDefault(option => option.Name is "I" or "i" or "replace");
        if (replace.Name is not null)
        {
            var token = replace.Value?.Text is { Length: > 0 } explicitToken ? explicitToken : "{}";
            inner = inner.Select(field => !field.Dynamic && field.Text.Contains(token, StringComparison.Ordinal)
                ? field with { Text = field.Text[..field.Text.IndexOf(token, StringComparison.Ordinal)], Dynamic = true, Display = field.Text }
                : field).ToList();
        }
        else
            inner.Add(input);

        if (inner[0].Dynamic)
            return Refuse("xargs", scope, extras, text, "xargs: the program is computed from its input");
        return Invocation(inner, Via(scope, "xargs"), extras with { Stdin = Stdin.Unknown }, nesting, text);
    }

    private StateChange? Sudo(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        if (!Options.Parse(args, Tables.Sudo, out var options, out var operand, out var problem))
            return Refuse("sudo", Elevate(scope, "sudo"), extras, text, $"sudo: {problem}");
        var elevated = Elevate(scope, "sudo");
        var operands = args.Skip(operand).ToList();
        bool Has(params string[] names) => options.Any(option => names.Contains(option.Name));

        if (Has("l", "list", "v", "validate", "k", "K", "reset-timestamp", "remove-timestamp", "V", "version", "h", "help"))
        {
            var facts = NewFacts("sudo", elevated);
            facts.Effects |= ActionEffects.Read;
            Emit(facts, text);
            return null;
        }

        if (Has("e", "edit"))
        {
            var facts = NewFacts("sudoedit", elevated);
            facts.Effects |= ActionEffects.Elevated;
            foreach (var file in operands)
                facts.Path(file, TargetAccess.Write);
            Emit(facts, text);
            return null;
        }

        if (Has("R", "chroot"))
            return Refuse("sudo", elevated, extras, text, "sudo --chroot: no path can be resolved");
        if (operands.Count == 0)
            return Refuse("sudo", elevated, extras, text, Has("i", "s", "login", "shell")
                ? "sudo starts an interactive shell" : "sudo without a command");
        if (options.FirstOrDefault(option => option.Name is "D" or "chdir").Value is { } directory)
            elevated = elevated with { Cwds = directory.Dynamic ? [null] : NewFacts("sudo", scope).Directories(directory) };
        // sudo resets the environment and sets its own HOME, so a nested shell sees none of the values Corvid tracked.
        elevated = elevated with { Variables = elevated.Variables.ToImmutableDictionary(pair => pair.Key, _ => (string?)null, StringComparer.Ordinal) };
        return Invocation(operands, elevated, extras, nesting, text);
    }

    private StateChange? Doas(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        if (!Options.Parse(args, Tables.Doas, out var options, out var operand, out var problem))
            return Refuse("doas", Elevate(scope, "doas"), extras, text, $"doas: {problem}");
        var operands = args.Skip(operand).ToList();
        if (options.Any(option => option.Name == "C"))
        {
            var facts = NewFacts("doas", Elevate(scope, "doas"));
            facts.Effects |= ActionEffects.Read;
            Emit(facts, text);
            return null;
        }

        if (operands.Count == 0)
            return Refuse("doas", Elevate(scope, "doas"), extras, text, "doas starts an interactive shell");
        return Invocation(operands, Elevate(scope, "doas"), extras, nesting, text);
    }

    private StateChange? Su(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        var filtered = args.Where(arg => arg.Dynamic || arg.Text != "-").ToList();
        if (!Options.Parse(filtered, Tables.Su, out var options, out _, out var problem))
            return Refuse("su", Elevate(scope, "su"), extras, text, $"su: {problem}");
        var command = options.LastOrDefault(option => option.Name is "c" or "command").Value;
        if (command is null)
            return Refuse("su", Elevate(scope, "su"), extras, text, "su starts an interactive shell");
        if (command.Dynamic)
            return Refuse("su", Elevate(scope, "su"), extras, text, "su -c: the script is computed at run time");
        NestedScript("su", command.Text, ChildShell(Elevate(scope, "su -c")), nesting, text);
        return null;
    }

    private StateChange? ChangeDirectory(string verb, List<Field> args, Scope scope, Extras extras, string text)
    {
        var facts = NewFacts(verb, scope);
        facts.Effects |= ActionEffects.Read;
        facts.AddRedirects(extras.Redirects);
        ImmutableArray<string?> cwds;
        var operands = args.Where(arg => arg.Dynamic || !(arg.Text.Length > 1 && arg.Text[0] == '-' && arg.Text.Skip(1).All(c => c is 'L' or 'P' or 'e' or '@'))).ToList();
        if (verb != "cd" || operands.Count > 1)
            cwds = [null];
        else if (operands.Count == 0)
            cwds = [_env.Home];
        else if (operands[0].Dynamic || operands[0].Text == "-")
            cwds = [null];
        else
        {
            cwds = facts.Directories(operands[0]);
            facts.Path(operands[0], TargetAccess.Read);
        }

        Emit(facts, text);
        return new StateChange(Cwds: cwds);
    }

    private StateChange? Declaration(string verb, List<Field> args, Scope scope, Extras extras, string text)
    {
        var assignments = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
        var exports = new List<string>();
        foreach (var arg in args)
        {
            if (!arg.Dynamic && arg.Text.StartsWith('-'))
                continue;
            var equals = arg.Text.IndexOf('=', StringComparison.Ordinal);
            var name = equals > 0 ? arg.Text[..equals] : arg.Text;
            if (!ShellParserNames.IsName(name))
                continue;
            // Only "export NAME=value" is followed; the others can change attributes or read input, so the value is unknown.
            assignments[name] = verb == "export" && equals > 0 && !arg.Dynamic ? arg.Text[(equals + 1)..]
                : verb == "export" && equals < 0 && scope.Variables.TryGetValue(name, out var existing) ? existing : null;
            if (verb == "export")
                exports.Add(name);
        }

        if (extras.Redirects.Length > 0)
        {
            var facts = NewFacts(verb, scope);
            facts.AddRedirects(extras.Redirects);
            Emit(facts, text);
        }

        return new StateChange(Assignments: assignments.ToImmutable(), Exports: [.. exports]);
    }

    private StateChange? Eval(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        if (args.Any(arg => arg.Dynamic))
            return Refuse("eval", scope, extras, text, "eval: the script is computed at run time");
        NestedScript("eval", string.Join(' ', args.Select(arg => arg.Text)), Via(scope, "eval"), nesting, text);
        return null;
    }

    private void Shell(string verb, List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        var command = false;
        var readsStdin = false;
        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            if (arg.Dynamic)
            {
                Refuse(verb, scope, extras, text, $"{verb}: option '{arg.Display}' is computed at run time");
                return;
            }

            var option = arg.Text;
            if (option is "--" or "-")
            {
                i++;
                break;
            }

            if (option.StartsWith("--", StringComparison.Ordinal))
            {
                if (option is "--norc" or "--noprofile" or "--login" or "--posix" or "--restricted" or "--verbose" or "--noediting")
                {
                    i++;
                    continue;
                }

                Refuse(verb, scope, extras, text, $"{verb}: unknown option '{option}'");
                return;
            }

            if (option.Length < 2 || option[0] is not ('-' or '+'))
                break;
            var takesValue = false;
            foreach (var flag in option.AsSpan(1))
            {
                if (flag == 'c' && option[0] == '-')
                    command = true;
                else if (flag is 'o' or 'O')
                    takesValue = true;
                else if (flag == 's')
                    readsStdin = true;
                else if (!"abefhkmnptuvxBCEHPTilr".Contains(flag))
                {
                    Refuse(verb, scope, extras, text, $"{verb}: unknown option '-{flag}'");
                    return;
                }
            }

            i += takesValue ? 2 : 1;
        }

        var operands = args.Skip(i).ToList();
        var child = ChildShell(Via(scope, command ? $"{verb} -c" : verb));
        if (command)
        {
            if (operands.Count == 0)
                Refuse(verb, scope, extras, text, $"{verb} -c without a script");
            else if (operands[0].Dynamic)
                Refuse(verb, scope, extras, text, $"{verb} -c: the script is computed at run time");
            else
                NestedScript(verb, operands[0].Text, child, nesting, text, extras.Redirects);
        }
        else if (operands.Count > 0 && !readsStdin)
        {
            var facts = NewFacts(verb, scope);
            facts.Effects |= ActionEffects.Execute;
            facts.Path(operands[0], TargetAccess.Execute);
            facts.AddRedirects(extras.Redirects);
            Emit(facts, text);
        }
        else if (extras.Stdin is { Kind: StdinKind.Literal, Text: { } script })
            NestedScript(verb, script, child, nesting, text, extras.Redirects);
        else
            Refuse(verb, scope, extras, text, $"{verb} reads commands from its input");
    }

    /// <summary>A new shell process: it inherits exported and well-known variables only.</summary>
    private Scope ChildShell(Scope scope) => scope with
    {
        Variables = scope.Variables.ToImmutableDictionary(pair => pair.Key,
            pair => _env.Variables.ContainsKey(pair.Key) || scope.Exported.Contains(pair.Key) ? pair.Value : null, StringComparer.Ordinal),
    };

    private void NestedScript(string verb, string script, Scope scope, int nesting, string text,
        ImmutableArray<(Field Target, bool Writes)> redirects = default)
    {
        if (!redirects.IsDefaultOrEmpty)
        {
            var facts = NewFacts(verb, scope);
            facts.AddRedirects(redirects);
            Emit(facts, text);
        }

        switch (ShellParser.Parse(script))
        {
            case ShellParsed parsed:
                Nested(() => Script(parsed.Script, scope, nesting + 1), nesting, verb);
                break;
            case ShellUnsupported unsupported:
                Opaque(script);
                Refuse(verb, scope, Extras.None, text, $"{verb}: inner script is outside the parsed subset: {unsupported.Reason}");
                break;
        }
    }

    private void Find(List<Field> args, Scope scope, Extras extras, int nesting, string text)
    {
        var i = 0;
        while (i < args.Count && !args[i].Dynamic)
        {
            var option = args[i].Text;
            if (option is "-H" or "-L" or "-P")
                i++;
            else if (option == "-D")
                i += 2;
            else if (option.Length > 2 && option.StartsWith("-O", StringComparison.Ordinal) && option[2..].All(char.IsAsciiDigit))
                i++;
            else
                break;
        }

        var roots = new List<Field>();
        while (i < args.Count && (args[i].Dynamic || !(args[i].Text.StartsWith('-') || args[i].Text is "(" or ")" or "!" or ",")))
            roots.Add(args[i++]);
        if (roots.Count == 0)
            roots.Add(Field.Literal("."));

        var facts = NewFacts("find", scope);
        foreach (var root in roots)
            facts.Path(root, TargetAccess.Read, recursive: true);
        for (; i < args.Count; i++)
        {
            var token = args[i];
            if (token.Dynamic)
            {
                facts.Unresolved($"find expression '{token.Display}' is computed at run time");
                continue;
            }

            switch (token.Text)
            {
                case "-delete":
                    foreach (var root in roots)
                        facts.Path(root, TargetAccess.Delete, recursive: true);
                    break;
                case "-fprint" or "-fprint0" or "-fls" or "-fprintf" when i + 1 < args.Count:
                    facts.Path(args[++i], TargetAccess.Write);
                    break;
                case "-exec" or "-execdir" or "-ok" or "-okdir":
                {
                    var end = args.FindIndex(i + 1, arg => !arg.Dynamic && arg.Text is ";" or "+");
                    if (end < 0)
                    {
                        facts.Unresolved($"find {token.Text} is not terminated");
                        i = args.Count;
                        break;
                    }

                    var inner = args.GetRange(i + 1, end - i - 1);
                    i = end;
                    if (inner.Count == 0)
                        break;
                    var inDirectory = token.Text is "-execdir" or "-okdir";
                    foreach (var root in roots)
                    {
                        // "{}" is any path under the root: a pattern below it, or a name in an unknown directory for -execdir.
                        var match = root.Dynamic || inDirectory
                            ? Field.Unknown("{}")
                            : new Field(root.Text.TrimEnd('/') + "/*", false, true, "{}");
                        var substituted = inner.Select(field => !field.Dynamic && field.Text.Contains("{}", StringComparison.Ordinal)
                            ? field.Text == "{}" ? match : Field.Unknown(field.Text)
                            : field).ToList();
                        var innerScope = Via(scope, "find " + token.Text);
                        if (inDirectory)
                            innerScope = innerScope with { Cwds = [null] };
                        Nested(() => Invocation(substituted, innerScope, Extras.None, nesting + 1, text), nesting, "find");
                    }

                    break;
                }
            }
        }

        facts.AddRedirects(extras.Redirects);
        Emit(facts, text);
    }

    // Expansion ------------------------------------------------------------------------------------------------------

    /// <summary>Expands a word as bash would, statically: tilde, known variables, field splitting of unquoted expansions.</summary>
    private List<Field> Expand(ShellWord word, Scope scope, List<ShellScript> substitutions)
    {
        var fields = new List<Field>();
        var text = new StringBuilder();
        var display = new StringBuilder();
        var dynamic = false;
        var glob = false;
        var content = false;

        void Emit()
        {
            if (text.Length > 0 || content || dynamic)
                fields.Add(new Field(text.ToString(), dynamic, glob, display.ToString()));
            text.Clear();
            display.Clear();
            dynamic = glob = content = false;
        }

        void Append(string value, bool quoted)
        {
            if (!dynamic)
                text.Append(value);
            display.Append(value);
            if (!quoted && value.AsSpan().IndexOfAny("*?[") >= 0)
                glob = true;
        }

        foreach (var part in word.Parts)
        {
            switch (part)
            {
                case LiteralPart literal:
                    Append(literal.Text, literal.Quoted);
                    content |= literal.Quoted;
                    break;
                case TildePart:
                    Append(_env.Home, quoted: true);
                    content = true;
                    break;
                case ParameterPart parameter:
                {
                    var value = Lookup(parameter.Name, scope);
                    if (value is null)
                    {
                        dynamic = true;
                        display.Append("$" + parameter.Name);
                        content = true;
                    }
                    else if (parameter.Quoted)
                    {
                        Append(value, quoted: true);
                        content = true;
                    }
                    else
                    {
                        var pieces = value.Split([' ', '\t', '\n']);
                        for (var i = 0; i < pieces.Length; i++)
                        {
                            if (i > 0)
                                Emit();
                            Append(pieces[i], quoted: false);
                        }
                    }

                    break;
                }
                case SubstitutionPart substitution:
                    substitutions.Add(substitution.Script);
                    dynamic = true;
                    display.Append("$(…)");
                    content = true;
                    break;
            }
        }

        Emit();
        return fields;
    }

    /// <summary>A word without field splitting (assignments, here-strings): its exact text, or null when dynamic.</summary>
    private string? Joined(ShellWord word, Scope scope, List<ShellScript> substitutions)
    {
        var text = new StringBuilder();
        var known = true;
        foreach (var part in word.Parts)
        {
            switch (part)
            {
                case LiteralPart literal:
                    text.Append(literal.Text);
                    break;
                case TildePart:
                    text.Append(_env.Home);
                    break;
                case ParameterPart parameter:
                    if (Lookup(parameter.Name, scope) is { } value)
                        text.Append(value);
                    else
                        known = false;
                    break;
                case SubstitutionPart substitution:
                    substitutions.Add(substitution.Script);
                    known = false;
                    break;
            }
        }

        return known ? text.ToString() : null;
    }

    private static string? Lookup(string name, Scope scope)
    {
        if (scope.Variables.TryGetValue(name, out var value))
            return value;
        if (name == "PWD" && scope.Cwds is [{ } cwd])
            return cwd;
        return null;
    }
}

/// <summary>Name rules shared with the parser.</summary>
internal static class ShellParserNames
{
    public static bool IsName(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}

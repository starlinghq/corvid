namespace Corvid.Approvals.Tests;

/// <summary>
/// Launchers (tracers, namespaces and sandboxes, the service manager, shell strings) are unwrapped, so the command they run
/// is analysed as if it ran on its own; unwrapping never makes anything less likely to ask.
/// </summary>
public sealed class LauncherTests
{
    private static AnalyzedCommand Only(string command, string verb) =>
        Assert.Single(Fixtures.Analyze(command).Commands, c => c.Verb == verb);

    [Theory]
    [InlineData("strace rm -rf build", "strace")]
    [InlineData("strace -f -e trace=file -s 200 rm -rf build", "strace")]
    [InlineData("strace -E A=1 -o trace.txt -- rm -rf build", "strace")]
    [InlineData("ltrace -f -o calls.txt rm -rf build", "ltrace")]
    [InlineData("valgrind --tool=memcheck --leak-check=full rm -rf build", "valgrind")]
    [InlineData("perf stat -e cycles -r 3 rm -rf build", "perf stat")]
    [InlineData("perf record -g -o p.data -- rm -rf build", "perf record")]
    [InlineData("perf trace -s rm -rf build", "perf trace")]
    [InlineData("script -qc 'rm -rf build' /dev/null", "script -c")]
    [InlineData("script /dev/null -qc 'rm -rf build'", "script -c")]
    [InlineData("script -q -e --command='rm -rf build' log.txt", "script -c")]
    [InlineData("parallel rm -rf ::: build", "parallel")]
    [InlineData("parallel -j 2 'rm -rf {}' ::: build", "parallel")]
    [InlineData("parallel ::: 'rm -rf build'", "parallel")]
    [InlineData("systemd-run --user --scope rm -rf build", "systemd-run")]
    public void Launchers_unwrap_to_the_command_they_run(string command, string launcher)
    {
        var rm = Only(command, "rm");
        Assert.Contains(launcher, rm.Via);
        var target = Assert.Single(rm.Targets);
        Assert.Equal(Fixtures.Workspace + "/build", target.Value);
        Assert.Equal(TargetAccess.Delete, target.Access);
        Assert.True(target.Recursive);
    }

    [Fact]
    public void Script_c_is_analysed_as_the_shell_string_it_runs()
    {
        var rm = Only("script -qc 'rm -rf ~' /dev/null", "rm");
        var target = Assert.Single(rm.Targets);
        Assert.Equal(Fixtures.Home, target.Value);
        Assert.Equal(PathLocation.Outside, target.Location);
        Assert.True(target.Recursive);
        // The typescript goes to /dev/null, which changes nothing; without a file it is ./typescript.
        Assert.True(Only("script -qc 'rm -rf ~' /dev/null", "script").ReadOnly);
        Assert.Contains(Only("script -c ls", "script").Targets,
            t => t.Value == Fixtures.Workspace + "/typescript" && t.Access == TargetAccess.Write);
    }

    [Fact]
    public void Strace_reports_the_file_it_writes_and_the_processes_it_attaches_to()
    {
        var analysis = Fixtures.Analyze("strace -o out.txt ls");
        var strace = Assert.Single(analysis.Commands, c => c.Verb == "strace");
        var output = Assert.Single(strace.Targets);
        Assert.Equal(Fixtures.Workspace + "/out.txt", output.Value);
        Assert.Equal(TargetAccess.Write, output.Access);
        Assert.Equal(["strace"], Assert.Single(analysis.Commands, c => c.Verb == "ls").Via);

        var separate = Only("strace -ff -o out ls", "strace");
        Assert.Contains(separate.Targets, t => t.Pattern && t.Value == Fixtures.Workspace + "/out.*");

        var attached = Only("strace -p 1234,1235", "strace");
        Assert.Equal(new[] { "1234", "1235" }, attached.Targets.Where(t => t.Kind == TargetKind.Process).Select(t => t.Value).ToArray());
        Assert.Equal(new[] { "4321" }, Only("ltrace -p 4321", "ltrace").Targets.Select(t => t.Value).ToArray());
        Assert.Contains(Only("perf record -p 99", "perf").Targets, t => t.Kind == TargetKind.Process && t.Value == "99");
    }

    [Fact]
    public void Tracer_outputs_that_run_or_change_things_are_seen()
    {
        // -o '|cmd' pipes the trace to a shell command.
        var piped = Only("strace -o '|rm -rf build' ls", "rm");
        Assert.Equal(Fixtures.Workspace + "/build", Assert.Single(piped.Targets).Value);
        // Fault injection changes what the program does.
        Assert.NotEmpty(Only("strace -e inject=openat:error=ENOENT ls", "strace").Unresolved);
        Assert.NotEmpty(Only("strace --inject=all:delay_enter=1 ls", "strace").Unresolved);
        // -u runs the command as another user.
        Assert.True(Only("strace -u nobody ls", "ls").Effects.HasFlag(ActionEffects.Elevated));
        // -E sets a code-loading variable.
        Assert.NotEmpty(Only("strace -E LD_PRELOAD=./x.so ls", "ls").Unresolved);
        // perf record writes perf.data by default; valgrind expands %p in file names.
        Assert.Contains(Only("perf record ls", "perf").Targets, t => t.Value == Fixtures.Workspace + "/perf.data");
        Assert.Contains(Only("valgrind --log-file=vg.%p.log ls", "valgrind").Targets,
            t => t.Pattern && t.Value == Fixtures.Workspace + "/vg.*.log" && t.Access == TargetAccess.Write);
        Assert.Contains(Only("valgrind --tool=massif ls", "valgrind").Targets, t => t.Pattern && t.Value.EndsWith("/massif.out.*"));
    }

    [Theory]
    [InlineData("nsenter -t 1 -m ls")]
    [InlineData("nsenter --target=1 --mount --uts ls")]
    [InlineData("unshare -n ls")]
    [InlineData("unshare -rm --fork ls")]
    [InlineData("firejail --quiet ls")]
    [InlineData("bwrap --ro-bind /usr /usr --unshare-net ls")]
    [InlineData("systemd-run --user ls")]
    [InlineData("systemd-run --scope ls")]
    [InlineData("strace -f nsenter -t 1 -n ls")]
    public void Isolating_launchers_leave_what_they_run_unresolved(string command)
    {
        var analysis = Fixtures.Analyze(command);
        var ls = Assert.Single(analysis.Commands, c => c.Verb == "ls");
        Assert.NotEmpty(ls.Unresolved);
        Assert.Contains(analysis.Commands, c => c.Verb != "ls" && !c.Unresolved.IsEmpty);
    }

    [Theory]
    [InlineData("strace --frobnicate ls")]
    [InlineData("strace -K ls")]
    [InlineData("ltrace --frobnicate ls")]
    [InlineData("valgrind --db-command=x ls")]
    [InlineData("valgrind --frobnicate ls")]
    [InlineData("perf stat --pre ./x ls")]
    [InlineData("perf record --frobnicate ls")]
    [InlineData("nsenter --frobnicate ls")]
    [InlineData("unshare -Q ls")]
    [InlineData("firejail --join=other ls")]
    [InlineData("bwrap --frobnicate ls")]
    [InlineData("bwrap --args 3 ls")]
    [InlineData("systemd-run --frobnicate ls")]
    [InlineData("systemd-run -p ExecStartPre=/tmp/x ls")]
    [InlineData("systemd-run -H host ls")]
    [InlineData("script --frobnicate -c ls")]
    [InlineData("parallel --ssh host ls ::: a")]
    [InlineData("parallel -q ls ::: a")]
    [InlineData("runuser -Z -u bob -- ls")]
    [InlineData("runuser --us=bob -- ls")]
    [InlineData("runuser --comm=ls bob")]
    [InlineData("runuser -u bob -c ls")]
    [InlineData("runuser -u bob ls -l")]
    [InlineData("at -Z now <<< ls")]
    [InlineData("at --file=x now <<< ls")]
    [InlineData("gdb --frobnicate ls")]
    [InlineData("gdb --arg ls")]
    [InlineData("gdb -bat ls")]
    [InlineData("gdb --eval=run ls")]
    [InlineData("gdb -p1234 ls")]
    [InlineData("gdb -batch -ex")]
    [InlineData("gdb ls core 1234")]
    public void Unknown_launcher_options_are_refused_not_skipped(string command)
    {
        var analysis = Fixtures.Analyze(command);
        Assert.DoesNotContain(analysis.Commands, c => c.Verb == "ls");
        Assert.Contains(analysis.Commands, c => c.Unresolved.Any(reason => reason.Length > 0));
    }

    [Fact]
    public void Systemd_run_uses_the_service_managers_working_directory_and_user()
    {
        var user = Only("systemd-run --user rm -rf build", "rm");
        Assert.Equal(Fixtures.Home + "/build", Assert.Single(user.Targets).Value);
        Assert.False(user.Effects.HasFlag(ActionEffects.Elevated));
        var system = Only("systemd-run rm -rf build", "rm");
        Assert.Equal("/build", Assert.Single(system.Targets).Value);
        Assert.True(system.Effects.HasFlag(ActionEffects.Elevated));
        Assert.Equal(Fixtures.Workspace + "/build", Assert.Single(Only("systemd-run --user -d rm -rf build", "rm").Targets).Value);
        Assert.Equal("/srv/build", Assert.Single(Only("systemd-run --working-directory=/srv rm -rf build", "rm").Targets).Value);
        // The manager expands $VAR itself, so a quoted '$X' is not known statically.
        Assert.True(Assert.Single(Only("systemd-run --user rm -rf '$X'", "rm").Targets).Dynamic);
        Assert.True(Only("systemd-run --on-active=60 --user ls", "systemd-run").Effects.HasFlag(ActionEffects.Schedule));
    }

    [Fact]
    public void Parallel_substitutes_static_inputs_and_leaves_others_unknown()
    {
        var analysis = Fixtures.Analyze("parallel rm -rf ::: a b");
        Assert.Equal(new[] { Fixtures.Workspace + "/a", Fixtures.Workspace + "/b" },
            analysis.Commands.Where(c => c.Verb == "rm").SelectMany(c => c.Targets).Select(t => t.Value).ToArray());
        var mv = Only("parallel 'mv {} {.}.bak' ::: notes.txt", "mv");
        Assert.Contains(mv.Targets, t => t.Value == Fixtures.Workspace + "/notes.txt" && t.Access == TargetAccess.Delete);
        Assert.Contains(mv.Targets, t => t.Value == Fixtures.Workspace + "/notes.bak" && t.Access == TargetAccess.Write);
        Assert.Equal(4, Fixtures.Analyze("parallel echo '{1}' '{2}' ::: a b ::: c d").Commands.Count(c => c.Verb == "echo"));
        // From stdin or a file, the input is known only at run time.
        var piped = Only("ls | parallel rm", "rm");
        Assert.True(Assert.Single(piped.Targets).Dynamic);
        Assert.Contains(Only("parallel -a list.txt rm", "parallel").Targets, t => t.Value == Fixtures.Workspace + "/list.txt");
        Assert.Contains(Fixtures.Analyze("parallel 'rm {=s/a/b/=}' ::: a").Commands, c => c.Verb == "parallel" && !c.Unresolved.IsEmpty);
        Assert.DoesNotContain(Fixtures.Analyze("parallel 'rm {=s/a/b/=}' ::: a").Commands, c => c.Verb == "rm");
    }

    // strace's long options are read as strace.c declares them: --verbose takes the next word, and an option with an
    // optional value (--quiet, --timestamps, --decode-fds) takes only "=VALUE", so the command is the word strace runs.
    [Theory]
    [InlineData("strace --verbose echo rm -rf build")]
    [InlineData("strace --quiet rm -rf -- echo build")]
    [InlineData("strace --timestamps rm -rf -- echo build")]
    [InlineData("strace --decode-fds rm -rf -- echo build")]
    public void Strace_long_options_take_values_as_strace_reads_them(string command)
    {
        var rm = Only(command, "rm");
        Assert.Contains("strace", rm.Via);
        Assert.Contains(rm.Targets, t => t.Access == TargetAccess.Delete);
    }

    [Fact]
    public void Ltrace_output_is_a_file_even_when_it_starts_with_a_pipe()
    {
        var ltrace = Only("ltrace -o '|ls' ls", "ltrace");
        Assert.Contains(ltrace.Targets, t => t.Value == Fixtures.Workspace + "/|ls" && t.Access == TargetAccess.Write);
    }

    [Fact]
    public void Script_c_asks_when_SHELL_is_inherited()
    {
        // script runs the command with $SHELL, which may be zsh or fish; only a shell set here is known.
        Assert.NotEmpty(Only("script -qc ls /dev/null", "script").Unresolved);
        Assert.Empty(Only("SHELL=/bin/bash script -qc ls /dev/null", "script").Unresolved);
    }

    [Fact]
    public void Valgrind_always_asks_but_still_reads_the_command_it_runs()
    {
        // ./.valgrindrc, ~/.valgrindrc and $VALGRIND_OPTS can redirect --log-file or set --trace-children.
        Assert.NotEmpty(Only("valgrind ls", "valgrind").Unresolved);
        var rm = Only("valgrind rm -rf build", "rm");
        Assert.Contains("valgrind", rm.Via);
        Assert.Empty(rm.Unresolved);
        Assert.Equal(Fixtures.Workspace + "/build", Assert.Single(rm.Targets).Value);
    }

    [Theory]
    [InlineData("runuser -u bob -- rm -rf build", "runuser -u")]
    [InlineData("runuser -u bob rm build", "runuser -u")]
    [InlineData("runuser --user=bob -- rm -rf build", "runuser -u")]
    [InlineData("runuser -c 'rm -rf build' bob", "runuser -c")]
    [InlineData("runuser bob --session-command='rm -rf build'", "runuser -c")]
    [InlineData("runuser -s /bin/bash -c 'rm -rf build'", "runuser -c")]
    public void Runuser_unwraps_and_elevates_what_it_runs(string command, string via)
    {
        var rm = Only(command, "rm");
        Assert.Contains(via, rm.Via);
        Assert.True(rm.Effects.HasFlag(ActionEffects.Elevated));
        Assert.Equal(Fixtures.Workspace + "/build", Assert.Single(rm.Targets).Value);
    }

    [Fact]
    public void Runuser_login_starts_in_an_unknown_directory_and_unknown_shells_are_unresolved()
    {
        Assert.Equal(PathLocation.Unresolved, Assert.Single(Only("runuser -l bob -c 'rm -rf build'", "rm").Targets).Location);
        Assert.Equal(PathLocation.Unresolved, Assert.Single(Only("runuser - bob -c 'rm -rf build'", "rm").Targets).Location);
        Assert.NotEmpty(Only("runuser bob -c ls", "runuser").Unresolved);
        Assert.Empty(Only("runuser -s /bin/sh bob -c ls", "runuser").Unresolved);
        // Arguments after the user go to the shell, which reads them as a script file.
        Assert.DoesNotContain(Fixtures.Analyze("runuser bob x.sh -c ls").Commands, c => c.Verb == "ls");
        Assert.DoesNotContain(Fixtures.Analyze("runuser -u bob").Commands, c => c.Unresolved.IsEmpty);
    }

    [Theory]
    [InlineData("echo 'rm -rf build' | at now")]
    [InlineData("echo rm -rf build | at now + 1 hour")]
    [InlineData("echo -n 'rm -rf build' | at -m teatime tomorrow")]
    [InlineData("at now <<< 'rm -rf build'")]
    [InlineData("at -t 202610211200 <<< 'rm -rf build'")]
    [InlineData("at now -q c <<< 'rm -rf build'")]
    [InlineData("batch <<'EOF'\nrm -rf build\nEOF")]
    [InlineData("at -b <<< 'rm -rf build'")]
    public void At_reads_a_static_job_as_a_script_that_runs_later(string command)
    {
        var analysis = Fixtures.Analyze(command);
        var rm = Assert.Single(analysis.Commands, c => c.Verb == "rm");
        Assert.True(rm.Via.Contains("at") || rm.Via.Contains("batch"));
        Assert.Equal(Fixtures.Workspace + "/build", Assert.Single(rm.Targets).Value);
        // The time spec is consumed, never read as a command, and the job itself always asks.
        var at = Assert.Single(analysis.Commands, c => c.Verb is "at" or "batch");
        Assert.True(at.Effects.HasFlag(ActionEffects.Schedule));
        Assert.NotEmpty(at.Unresolved);
        Assert.All(analysis.Commands, c => Assert.Contains(c.Verb, new[] { "echo", "at", "batch", "rm" }));
    }

    [Fact]
    public void At_jobs_it_cannot_read_ask_and_its_queue_commands_are_their_own_verbs()
    {
        var file = Only("at -f job.sh now", "at");
        Assert.Contains(file.Targets, t => t.Value == Fixtures.Workspace + "/job.sh" && t.Access == TargetAccess.Read);
        Assert.Contains(file.Unresolved, reason => reason.Contains("job.sh", StringComparison.Ordinal));
        // Only a pipe from echo of static words is known; anything else is input Corvid cannot see.
        Assert.DoesNotContain(Fixtures.Analyze("cat job | at now").Commands, c => c.Verb == "rm");
        Assert.DoesNotContain(Fixtures.Analyze("echo -e 'rm\\x20-rf build' | at now").Commands, c => c.Verb == "rm");
        Assert.DoesNotContain(Fixtures.Analyze("echo rm -rf * | at now").Commands, c => c.Verb == "rm");
        Assert.DoesNotContain(Fixtures.Analyze("echo \"rm -rf $X\" | at now").Commands, c => c.Verb == "rm");
        Assert.DoesNotContain(Fixtures.Analyze("echo rm -rf build > log | at now").Commands, c => c.Verb == "rm");
        // A pipe from echo is read only by at: a shell reading it still asks as before.
        Assert.DoesNotContain(Fixtures.Analyze("echo 'rm -rf build' | bash").Commands, c => c.Verb == "rm");
        Assert.True(Only("at -l", "atq").ReadOnly);
        Assert.True(Only("atq", "atq").ReadOnly);
        Assert.True(Only("atq -q b", "atq").ReadOnly);
        Assert.True(Only("at -r 3", "atrm").Effects.HasFlag(ActionEffects.Schedule));
        Assert.True(Only("atrm 3", "atrm").Effects.HasFlag(ActionEffects.Schedule));
    }

    [Theory]
    [InlineData("gdb --args rm -rf build")]
    [InlineData("gdb -q -batch -ex run --args rm -rf build")]
    [InlineData("gdb --batch --eval-command=run --args rm -rf build")]
    [InlineData("gdb -nx -- rm")]
    public void Gdb_reads_the_program_it_debugs_and_always_asks(string command)
    {
        var analysis = Fixtures.Analyze(command);
        var rm = Assert.Single(analysis.Commands, c => c.Verb == "rm");
        Assert.Contains("gdb", rm.Via);
        Assert.NotEmpty(Assert.Single(analysis.Commands, c => c.Verb == "gdb").Unresolved);
    }

    [Fact]
    public void Gdb_options_add_targets()
    {
        Assert.Equal(Fixtures.Workspace + "/build", Assert.Single(Only("gdb --args rm -rf build", "rm").Targets).Value);
        Assert.Equal("/srv/build", Assert.Single(Only("gdb -cd=/srv --args rm -rf build", "rm").Targets).Value);
        Assert.Contains(Only("gdb -p 1234", "gdb").Targets, t => t.Kind == TargetKind.Process && t.Value == "1234");
        Assert.Contains(Only("gdb --pid=1234", "gdb").Targets, t => t.Kind == TargetKind.Process && t.Value == "1234");
        Assert.Contains(Only("gdb prog 4321", "gdb").Targets, t => t.Kind == TargetKind.Process && t.Value == "4321");
        var core = Only("gdb prog core.123", "gdb");
        Assert.Contains(core.Targets, t => t.Value == Fixtures.Workspace + "/core.123" && t.Access == TargetAccess.Read);
        Assert.Contains(Only("gdb -c core prog", "gdb").Targets, t => t.Value == Fixtures.Workspace + "/core" && t.Access == TargetAccess.Read);
        var script = Only("gdb -batch -x cmds.gdb prog", "gdb");
        Assert.Contains(script.Targets, t => t.Value == Fixtures.Workspace + "/cmds.gdb" && t.Access == TargetAccess.Read);
        Assert.Contains(script.Unresolved, reason => reason.Contains("cmds.gdb", StringComparison.Ordinal));
        // -ex text is kept for the hardline backstop.
        var ex = Fixtures.Analyze("gdb -batch -ex 'shell id' ls");
        Assert.Contains("shell id", ex.OpaqueScripts);
        Assert.Contains(Assert.Single(ex.Commands, c => c.Verb == "gdb").Unresolved, reason => reason.Contains("shell id", StringComparison.Ordinal));
    }
}

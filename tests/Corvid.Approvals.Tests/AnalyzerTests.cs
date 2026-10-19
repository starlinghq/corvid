namespace Corvid.Approvals.Tests;

public sealed class AnalyzerTests
{
    private static AnalyzedCommand Only(string command, string verb) =>
        Assert.Single(Fixtures.Analyze(command).Commands, c => c.Verb == verb);

    [Theory]
    [InlineData("timeout 5 rm -rf build", "timeout")]
    [InlineData("timeout -s KILL -k 2 5s rm -rf build", "timeout")]
    [InlineData("env FOO=1 rm -rf build", "env")]
    [InlineData("env -i -u PATH rm -rf build", "env")]
    [InlineData("nice -n 19 rm -rf build", "nice")]
    [InlineData("nice -10 rm -rf build", "nice")]
    [InlineData("nohup rm -rf build", "nohup")]
    [InlineData("command rm -rf build", "command")]
    [InlineData("exec rm -rf build", "exec")]
    [InlineData("builtin command rm -rf build", "builtin")]
    [InlineData("time -p rm -rf build", "time")]
    [InlineData("stdbuf -oL rm -rf build", "stdbuf")]
    [InlineData("setsid -f rm -rf build", "setsid")]
    [InlineData("ionice -c 3 rm -rf build", "ionice")]
    [InlineData("sh -c 'rm -rf build'", "sh -c")]
    [InlineData("bash -lc \"rm -rf build\"", "bash -c")]
    [InlineData("bash -o pipefail -ec 'rm -rf build'", "bash -c")]
    [InlineData("eval rm -rf build", "eval")]
    [InlineData("bash <<< 'rm -rf build'", "bash")]
    [InlineData("timeout 5 env A=1 nice sh -c \"bash -c 'rm -rf build'\"", "timeout")]
    public void Wrappers_unwrap_to_the_command_they_run(string command, string wrapper)
    {
        var rm = Only(command, "rm");
        Assert.Contains(wrapper, rm.Via);
        var target = Assert.Single(rm.Targets);
        Assert.Equal(Fixtures.Workspace + "/build", target.Value);
        Assert.Equal(TargetAccess.Delete, target.Access);
        Assert.True(target.Recursive);
        Assert.Equal(PathLocation.Inside, target.Location);
        Assert.True(rm.Effects.HasFlag(ActionEffects.Recursive));
    }

    [Fact]
    public void Sudo_elevates_and_unwraps()
    {
        var rm = Only("sudo -u root -E rm -rf /tmp/x", "rm");
        Assert.True(rm.Effects.HasFlag(ActionEffects.Elevated));
        Assert.Equal(["sudo"], rm.Via);
        Assert.True(Only("doas rm x", "rm").Effects.HasFlag(ActionEffects.Elevated));
        var su = Only("su root -c 'rm -rf /tmp/x'", "rm");
        Assert.True(su.Effects.HasFlag(ActionEffects.Elevated));
    }

    [Fact]
    public void Unknown_wrapper_options_are_refused_not_skipped()
    {
        var timeout = Assert.Single(Fixtures.Analyze("timeout --frobnicate 5 rm -rf /").Commands);
        Assert.Equal("timeout", timeout.Verb);
        Assert.Contains(timeout.Unresolved, reason => reason.Contains("unknown option"));
        Assert.Contains(Fixtures.Analyze("env -S 'rm -rf /'").Commands[0].Unresolved, reason => reason.Contains("-S"));
        Assert.Contains(Fixtures.Analyze("sh $FLAGS 'rm -rf /'").Commands[0].Unresolved, reason => reason.Contains("computed at run time"));
    }

    [Fact]
    public void Cd_is_followed_and_conditional_cd_keeps_both_directories()
    {
        var after = Only("cd / && rm -rf *", "rm");
        Assert.Equal("/*", Assert.Single(after.Targets).Value);
        var either = Only("cd build; rm -rf *", "rm");
        Assert.Equal([Fixtures.Workspace + "/*", Fixtures.Workspace + "/build/*"], either.Targets.Select(t => t.Value).Order());
        var relative = Only("cd sub && rm -rf ../out", "rm");
        Assert.Equal(Fixtures.Workspace + "/out", Assert.Single(relative.Targets).Value);
        var unknown = Only("cd \"$DIR\" && rm -rf out", "rm");
        Assert.Equal(PathLocation.Unresolved, Assert.Single(unknown.Targets).Location);
    }

    [Fact]
    public void Variables_resolve_split_and_go_unknown()
    {
        Assert.Equal(Fixtures.Home, Assert.Single(Only("rm -rf $HOME", "rm").Targets).Value);
        Assert.Equal(Fixtures.Home, Assert.Single(Only("rm -rf \"${HOME}\"", "rm").Targets).Value);
        var split = Only("X='-rf /'; rm $X", "rm");
        Assert.True(split.Effects.HasFlag(ActionEffects.Recursive));
        Assert.Equal("/", Assert.Single(split.Targets).Value);
        var conditional = Only("X=build; true || X=/; rm -rf $X", "rm");
        Assert.True(Assert.Single(conditional.Targets).Dynamic);
        var unknown = Only("rm -rf \"$TARGET\"", "rm");
        Assert.True(Assert.Single(unknown.Targets).Dynamic);
        // A shell variable is not exported to a child shell, so the child's value is unknown.
        Assert.True(Assert.Single(Only("X=build; sh -c 'rm -rf $X'", "rm").Targets).Dynamic);
        Assert.Equal("/", Assert.Single(Only("export X=/; sh -c 'rm -rf $X'", "rm").Targets).Value);
        Assert.Equal("/", Assert.Single(Only("X=/ sh -c 'rm -rf $X'", "rm").Targets).Value);
    }

    [Fact]
    public void Substitutions_run_and_make_their_word_dynamic()
    {
        var analysis = Fixtures.Analyze("rm -rf $(cat list.txt) && echo `whoami`");
        var cat = Assert.Single(analysis.Commands, c => c.Verb == "cat");
        Assert.True(cat.Substitution);
        Assert.Contains(analysis.Commands, c => c.Verb == "whoami" && c.Substitution);
        Assert.True(Assert.Single(analysis.Commands.Single(c => c.Verb == "rm").Targets).Dynamic);
        var computed = Assert.Single(Fixtures.Analyze("$(echo rm) -rf /").Commands, c => c.Verb == "?");
        Assert.NotEmpty(computed.Unresolved);
    }

    [Fact]
    public void Xargs_and_find_give_their_inner_commands_unknown_or_pattern_targets()
    {
        var xargs = Only("find . -name '*.o' | xargs rm -f", "rm");
        Assert.Contains("xargs", xargs.Via);
        Assert.True(Assert.Single(xargs.Targets).Dynamic);
        var exec = Only("find / -name core -exec rm -rf {} \\;", "rm");
        var target = Assert.Single(exec.Targets);
        Assert.Equal("/*", target.Value);
        Assert.Equal(PathLocation.Outside, target.Location);
        var delete = Only("find build -delete", "find");
        Assert.Contains(delete.Targets, t => t.Access == TargetAccess.Delete && t.Recursive && t.Value == Fixtures.Workspace + "/build");
    }

    [Fact]
    public void Globs_are_located_by_their_static_directory()
    {
        Assert.Equal(PathLocation.Inside, Assert.Single(Only("rm -rf build/*", "rm").Targets).Location);
        Assert.Equal(PathLocation.Inside, Assert.Single(Only("rm -rf *", "rm").Targets).Location);
        Assert.Equal(PathLocation.Outside, Assert.Single(Only("rm -rf /*", "rm").Targets).Location);
        Assert.Equal(PathLocation.Outside, Assert.Single(Only("rm -rf ~/*", "rm").Targets).Location);
        Assert.Equal(PathLocation.Outside, Assert.Single(Only("rm -rf ../*", "rm").Targets).Location);
        Assert.Equal(PathLocation.Unresolved, Assert.Single(Only("rm -rf /home/*/work/project/x", "rm").Targets).Location);
        Assert.Equal(PathLocation.Outside, Assert.Single(Only("rm -rf /home/user/work/proj*", "rm").Targets).Location);
    }

    [Fact]
    public void Targets_kinds_hosts_pids_and_redirections()
    {
        var kill = Only("kill -9 1234 -- -1", "kill");
        Assert.Equal(["1234", "-1"], kill.Targets.Select(t => t.Value));
        Assert.All(kill.Targets, t => Assert.Equal(TargetKind.Process, t.Kind));
        Assert.Equal("1234", Assert.Single(Only("kill 1234", "kill").Targets).Value);
        var pkill = Only("pkill -f node", "pkill");
        Assert.Equal(TargetKind.ProcessName, Assert.Single(pkill.Targets).Kind);

        var curl = Only("curl -fsSL -o out.json https://api.example.org/v1 -d @secrets.txt", "curl");
        Assert.Contains(curl.Targets, t => t.Kind == TargetKind.Host && t.Value == "api.example.org");
        Assert.Contains(curl.Targets, t => t.Access == TargetAccess.Write && t.Value == Fixtures.Workspace + "/out.json");
        Assert.Contains(curl.Targets, t => t.Access == TargetAccess.Read && t.Value == Fixtures.Workspace + "/secrets.txt");
        Assert.True(curl.Effects.HasFlag(ActionEffects.Network | ActionEffects.Remote));
        Assert.Contains(Only("ssh deploy@build.example.org uptime", "ssh").Targets, t => t.Value == "build.example.org");
        Assert.Contains(Only("rsync -a dist/ web:/srv/www", "rsync").Targets, t => t.Kind == TargetKind.Host && t.Value == "web");

        var echo = Only("echo hi > notes.txt 2>/dev/null", "echo");
        Assert.Equal(Fixtures.Workspace + "/notes.txt", Assert.Single(echo.Targets).Value);
        Assert.False(echo.ReadOnly);
        Assert.True(Only("cat README.md > /dev/null", "cat").ReadOnly);
    }

    [Theory]
    [InlineData("ls -la src")]
    [InlineData("git status")]
    [InlineData("git log --oneline -5")]
    [InlineData("git -C sub diff HEAD~1")]
    [InlineData("cat README.md | grep -n TODO | head -20")]
    [InlineData("rg -n 'fn main' src")]
    [InlineData("sed -n '1,20p' src/main.rs")]
    [InlineData("sed 's/foo/bar/g' a.txt")]
    [InlineData("awk '{print $1}' data.csv")]
    [InlineData("find . -name '*.cs' -type f")]
    [InlineData("wc -l $(git ls-files)")]
    [InlineData("python3 --version")]
    [InlineData("echo $HOME")]
    public void Read_only_commands_are_read_only(string command)
    {
        var analysis = Fixtures.Analyze(command);
        Assert.Null(analysis.Unsupported);
        Assert.All(analysis.Commands, c => Assert.True(c.ReadOnly, $"{c.Verb} is {c.Effects}"));
    }

    [Theory]
    [InlineData("sed -i 's/a/b/' file.txt", ActionEffects.Write)]
    [InlineData("sed 's/a/b/e' file.txt", ActionEffects.Execute)]
    [InlineData("sed '1w /tmp/out' file.txt", ActionEffects.Execute)]
    [InlineData("awk 'BEGIN { system(\"id\") }'", ActionEffects.Execute)]
    [InlineData("sort -o out.txt in.txt", ActionEffects.Write)]
    [InlineData("python3 script.py", ActionEffects.Execute)]
    [InlineData("./build.sh", ActionEffects.Execute)]
    [InlineData("frobnicate --all", ActionEffects.Execute)]
    [InlineData("git push origin main", ActionEffects.Network)]
    [InlineData("git frobnicate", ActionEffects.Execute)]
    [InlineData("git -c core.pager=less log", ActionEffects.Execute)]
    [InlineData("npm install", ActionEffects.Network)]
    [InlineData("systemctl stop nginx", ActionEffects.System)]
    [InlineData("crontab -e", ActionEffects.Schedule)]
    [InlineData("tar xzf a.tgz", ActionEffects.Write)]
    public void Side_effects_are_found(string command, ActionEffects expected)
    {
        var analysis = Fixtures.Analyze(command);
        Assert.True(analysis.Effects.HasFlag(expected), $"{command}: {analysis.Effects}");
        Assert.Contains(analysis.Commands, c => !c.ReadOnly);
    }

    [Fact]
    public void A_program_named_by_path_is_not_the_known_verb()
    {
        Assert.Equal("./rm", Assert.Single(Fixtures.Analyze("./rm -rf build").Commands).Verb);
        Assert.Equal("rm", Assert.Single(Fixtures.Analyze("/usr/bin/rm -rf build").Commands).Verb);
        Assert.Equal("rm", Assert.Single(Fixtures.Analyze("\\rm -rf build").Commands).Verb);
    }

    [Fact]
    public void Source_and_alias_make_later_commands_opaque()
    {
        var sourced = Fixtures.Analyze("source venv/bin/activate && ls");
        Assert.Contains(sourced.Commands.Single(c => c.Verb == "ls").Unresolved, reason => reason.Contains("source"));
        Assert.NotNull(Fixtures.Analyze("alias ls='rm -rf /'; ls").Unsupported);
        Assert.NotNull(Fixtures.Analyze("trap 'rm -rf /' EXIT").Unsupported);
    }
}

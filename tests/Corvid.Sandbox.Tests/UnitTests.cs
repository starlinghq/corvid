using Corvid.Sandbox.Launcher;

namespace Corvid.Sandbox.Tests;

public sealed class PolicyTests
{
    [Fact]
    public void Policy_maps_to_the_launcher_command_line()
    {
        SandboxPolicy policy = SandboxPolicy.ForWorkspace("/w") with
        {
            ReadOnlyPaths = ["/w/locked"],
            DeniedPaths = ["/data/secret"],
            Network = true,
            Environment = ["PATH", "LANG"],
        };
        var command = new SandboxCommand("git", "status", "--short") { WorkingDirectory = "/w/sub" };

        List<string> args = BubblewrapSandbox.Arguments(policy, command, "/s/status.json", new SandboxOptions { RequireKernelLayers = false, BubblewrapPath = "/opt/bwrap" });

        Assert.Equal(
            ["--workspace", "/w", "--chdir", "/w/sub", "--ro", "/w/locked", "--deny", "/data/secret", "--env", "PATH", "--env", "LANG",
             "--network", "--best-effort-layers", "--bwrap", "/opt/bwrap", "--status-file", "/s/status.json", "--", "git", "status", "--short"],
            args);

        // And the launcher reads it back as the same request.
        LaunchRequest request = Arguments.ParseLaunch(args);
        Assert.Equal("/w", request.Workspace);
        Assert.Equal("/w/sub", request.WorkingDirectory);
        Assert.Equal(["/w/locked"], request.ReadOnlyPaths);
        Assert.Equal(["/data/secret"], request.DeniedPaths);
        Assert.Equal(["PATH", "LANG"], request.Environment);
        Assert.True(request.Network);
        Assert.True(request.BestEffortLayers);
        Assert.Equal(["git", "status", "--short"], request.Command);
    }

    [Fact]
    public void A_prepared_launch_carries_only_the_policy_and_launcher_environment()
    {
        // C-018: a long-lived process (an MCP server) started by its caller, with the same command line and a cleared environment.
        var launcher = Path.Combine(Path.GetTempPath(), "corvid-sandbox-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(launcher, "");
        try
        {
            var sandbox = new BubblewrapSandbox(new SandboxOptions
            {
                LauncherPath = launcher,
                Environment = new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["HOME"] = "/home/u", ["GITHUB_TOKEN"] = "x", ["NOTES_DIR"] = "/n" },
            });
            var launch = sandbox.Prepare(SandboxPolicy.ForWorkspace("/w") with { Environment = ["PATH", "NOTES_DIR"] }, new SandboxCommand("notes-mcp", "--stdio"));
            Assert.Equal(launcher, launch.Launcher);
            Assert.Equal(["--workspace", "/w", "--env", "PATH", "--env", "NOTES_DIR", "--", "notes-mcp", "--stdio"], launch.Arguments);
            Assert.Equal(new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["HOME"] = "/home/u", ["NOTES_DIR"] = "/n" }, launch.Environment);
            Assert.Throws<ArgumentException>(() => sandbox.Prepare(SandboxPolicy.ForWorkspace("/w"), new SandboxCommand("x") { StandardInput = "in" }));
            Assert.Throws<ArgumentException>(() => sandbox.Prepare(SandboxPolicy.ForWorkspace("/w") with { Environment = ["GITHUB_TOKEN"] }, new SandboxCommand("x")));
        }
        finally
        {
            File.Delete(launcher);
        }
        Assert.Throws<SandboxRefusedException>(() => new BubblewrapSandbox(new SandboxOptions { LauncherPath = launcher })
            .Prepare(SandboxPolicy.ForWorkspace("/w"), new SandboxCommand("x")));
    }

    [Fact]
    public void Defaults_are_offline_with_a_minimal_environment_and_all_layers()
    {
        SandboxPolicy policy = SandboxPolicy.ForWorkspace("/w");

        List<string> args = BubblewrapSandbox.Arguments(policy, new SandboxCommand("true"), statusFile: null, new SandboxOptions());

        Assert.DoesNotContain("--network", args);
        Assert.DoesNotContain("--best-effort-layers", args);
        Assert.Equal(SandboxPolicy.DefaultEnvironment.Length, args.Count(a => a == "--env"));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("")]
    public void Relative_paths_are_rejected(string path)
    {
        Assert.Throws<ArgumentException>(() => SandboxPolicy.ForWorkspace(path).Validate());
        Assert.Throws<ArgumentException>(() => (SandboxPolicy.ForWorkspace("/w") with { ReadOnlyPaths = [path] }).Validate());
        Assert.Throws<ArgumentException>(() => (SandboxPolicy.ForWorkspace("/w") with { DeniedPaths = [path] }).Validate());
    }

    [Theory]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("AWS_PROFILE")]
    [InlineData("SSH_AUTH_SOCK")]
    [InlineData("GPG_AGENT_INFO")]
    [InlineData("DBUS_SESSION_BUS_ADDRESS")]
    [InlineData("DATABASE_PASSWORD")]
    [InlineData("npm_config__auth")]
    [InlineData("MY_PRIVATE_THING")]
    [InlineData("SIGNING_KEY")]
    [InlineData("1BAD")]
    [InlineData("WITH-DASH")]
    public void Secret_like_or_invalid_names_are_rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => (SandboxPolicy.ForWorkspace("/w") with { Environment = ["PATH", name] }).Validate());
    }

    [Fact]
    public void The_default_environment_passes()
    {
        SandboxPolicy.ForWorkspace("/w").Validate();
        (SandboxPolicy.ForWorkspace("/w") with { Environment = ["CI", "NO_COLOR", "PYTHONPATH", "GOPATH"] }).Validate();
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("HOME")]
    [InlineData("LANG")]
    [InlineData("NO_COLOR")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("SSH_AUTH_SOCK")]
    [InlineData("AUTHORIZATION")]
    [InlineData("KUBECONFIG")]
    [InlineData("XDG_SESSION_ID")]
    [InlineData("MONKEY")]
    public void Library_and_launcher_agree_on_secret_names(string name)
    {
        Assert.Equal(SandboxPlan.IsSecretLike(name), EnvironmentNames.IsSecretLike(name));
    }

    // The approval pipeline reads these from the library; the launcher's own copy is what actually hides the paths.
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("/data", "/conf", "/run/user/1000")]
    [InlineData("/home/u/.local/share", "/home/u/.config", null)]
    [InlineData("relative/data", "", "relative/run")]
    public void Library_and_launcher_agree_on_built_in_denials(string? data, string? config, string? runtime)
    {
        var variables = new Dictionary<string, string?> { ["XDG_DATA_HOME"] = data, ["XDG_CONFIG_HOME"] = config, ["XDG_RUNTIME_DIR"] = runtime };
        string? Env(string name) => variables.GetValueOrDefault(name);

        Assert.Equal(SandboxPlan.VaultDirectories(Env, "/home/u"), BuiltInDenials.VaultDirectories("/home/u", Env));
        Assert.Equal(SandboxPlan.SecretDirectories, BuiltInDenials.SecretDirectories);
        // A6 review H2: attached mode's tokens, default and under XDG_STATE_HOME (data's value stands in for it here).
        string? State(string name) => name == "XDG_STATE_HOME" ? data : Env(name);
        Assert.Equal(SandboxPlan.AttachStateDirectories(State, "/home/u"), BuiltInDenials.AttachStateDirectories("/home/u", State));
        string[] gh = config is { Length: > 0 } && Path.IsPathFullyQualified(config) ? [Path.Combine(config, "gh")] : [];
        Assert.Equal(SandboxPlan.SecretDirectories.Select(relative => Path.Combine("/home/u", relative)).Concat(gh).Distinct(),
            BuiltInDenials.SecretPaths("/home/u", Env));
        Assert.Equal(runtime is { } r && Path.IsPathFullyQualified(r) ? r : null, BuiltInDenials.RuntimeDirectory(Env));
        foreach (string name in (string[])[".env", ".env.local", ".env.example", ".envrc", "env", "x.env", ".env."])
        {
            Assert.Equal(SandboxPlan.IsEnvFileName(name), BuiltInDenials.IsEnvFileName(name));
        }
    }
}

public sealed class LauncherTests
{
    [Theory]
    [InlineData("--workspace")]
    [InlineData("--workspace /w")]
    [InlineData("--workspace /w --")]
    [InlineData("--workspace /w --bogus -- true")]
    [InlineData("-- true")]
    public void Malformed_command_lines_are_refused(string args)
    {
        Assert.Throws<RefusedException>(() => Arguments.ParseLaunch(args.Split(' ')));
    }

    // A5: a deny list naming the launcher (Corvid's state protection once did, for Sandbox:LauncherPath) hid it from its own
    // stage 2, so every command failed with execve's "Permission denied". The plan refuses with the reason instead.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Denying_the_launcher_itself_is_refused_with_the_reason(bool directory)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/bwrap"))
        {
            Assert.Skip("needs Linux and bubblewrap at /usr/bin/bwrap");
        }

        string root = Corvid.Tests.ShortTemp.Create("cv-launch-");
        try
        {
            string home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            string workspace = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
            string bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            string launcher = Path.Combine(bin, "corvid-sandbox");
            File.WriteAllText(launcher, "");
            string? Env(string name) => name switch { "HOME" => home, "PATH" => "/usr/bin:/bin", _ => null };
            var location = new LauncherLocation(launcher, bin, [launcher]);

            LaunchRequest request = Arguments.ParseLaunch(["--workspace", workspace, "--deny", directory ? bin : launcher, "--", "true"]);
            var refused = Assert.Throws<RefusedException>(() => SandboxPlan.Create(request, Env, location));
            Assert.Contains("hides corvid-sandbox itself", refused.Message, StringComparison.Ordinal);

            // Another path in the same place is hidden as before.
            File.WriteAllText(Path.Combine(bin, "other"), "");
            LaunchRequest other = Arguments.ParseLaunch(["--workspace", workspace, "--deny", Path.Combine(bin, "other"), "--", "true"]);
            Assert.Contains(Path.Combine(bin, "other"), SandboxPlan.Create(other, Env, location).MaskedFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Stage_2_reads_its_options()
    {
        Stage2Request request = Arguments.ParseStage2(
            ["--stage2", "--ready-fd", "7", "--rw", "/w", "--rw", "/tmp", "--env", "PATH=/bin", "--not-in", "pid=pid:[1]", "--", "sh", "-c", "x"]);

        Assert.Equal(7, request.ReadyFd);
        Assert.Equal(["/w", "/tmp"], request.WritablePaths);
        Assert.Equal(["PATH=/bin"], request.Environment);
        Assert.Equal(["pid=pid:[1]"], request.OutsideNamespaces);
        Assert.Equal(["sh", "-c", "x"], request.Command);
        Assert.Throws<RefusedException>(() => Arguments.ParseStage2(["--stage2", "--ready-fd", "1", "--", "sh"]));
    }

    [Fact]
    public void Masks_come_after_every_bind_and_parents_bind_before_children()
    {
        var plan = new SandboxPlan
        {
            Bubblewrap = "/usr/bin/bwrap",
            Workspace = "/home/u/project",
            WorkingDirectory = "/home/u/project",
            ReadOnlyPaths = ["/home/u/project/locked", "/home/u"],
            MaskedDirectories = ["/home/u/.ssh", "/home/u/project/secrets"],
            MaskedFiles = ["/home/u/project/.env"],
            Environment = ["PATH=/usr/bin"],
            Network = false,
            BestEffortLayers = false,
            Stage2 = ["/opt/corvid-sandbox"],
            Stage2Environment = [new("DOTNET_ROOT", "/opt/dotnet")],
            OutsideNamespaces = ["pid=pid:[1]"],
            Command = ["make"],
        };

        List<string> a = [.. plan.BubblewrapArguments(readyFd: 9)];

        int Index(string option, string path) => Enumerable.Range(0, a.Count - 1).First(i => a[i] == option && a[i + 1] == path);
        Assert.Equal(["--unshare-all", "--unshare-user", "--disable-userns"], a[..3]);
        Assert.DoesNotContain("--share-net", a);
        Assert.Contains("--die-with-parent", a);
        Assert.Contains("--new-session", a);
        Assert.Contains("--clearenv", a);
        int root = Index("--ro-bind", "/");
        int home = Index("--ro-bind", "/home/u");
        int workspace = Index("--bind", "/home/u/project");
        int locked = Index("--ro-bind", "/home/u/project/locked");
        Assert.True(root < home && home < workspace && workspace < locked, "binds must run parent to child");
        Assert.True(Index("--tmpfs", "/tmp") < workspace && Index("--tmpfs", "/run") < workspace);
        int firstMask = Index("--tmpfs", "/home/u/.ssh") - 2;
        Assert.Equal(["--perms", "0000"], a[firstMask..(firstMask + 2)]);
        Assert.True(locked < firstMask, "masks must come after every bind");
        Assert.True(Index("--tmpfs", "/home/u/project/secrets") > locked);
        Assert.True(Index("--ro-bind", "/dev/null") > locked);
        Assert.Equal("/home/u/project/.env", a[Index("--ro-bind", "/dev/null") + 2]);

        // bubblewrap runs stage 2, which gets the command's environment as arguments and execs the command.
        int separator = a.IndexOf("--");
        Assert.Equal(["--setenv", "DOTNET_ROOT", "/opt/dotnet"], a[(separator - 3)..separator]);
        Assert.Equal(
            ["/opt/corvid-sandbox", "--stage2", "--ready-fd", "9", "--rw", "/home/u/project", "--rw", "/tmp", "--rw", "/dev/shm",
             "--env", "PATH=/usr/bin", "--not-in", "pid=pid:[1]", "--", "make"],
            a[(separator + 1)..]);

        Assert.Contains("--share-net", (plan with { Network = true }).BubblewrapArguments(9));
    }

    [Fact]
    public void The_seccomp_program_checks_the_architecture_first_and_every_jump_stays_inside()
    {
        foreach (Seccomp.Table table in (Seccomp.Table[])[Seccomp.X64, Seccomp.Arm64])
        {
            Seccomp.Instruction[] program = Seccomp.Build(table);

            Assert.Equal(new Seccomp.Instruction(0x20, 0, 0, 4), program[0]);             // load arch
            Assert.Equal(new Seccomp.Instruction(0x15, 1, 0, table.AuditArch), program[1]);
            Assert.Equal(new Seccomp.Instruction(0x06, 0, 0, 0x80000000), program[2]);   // other arch: kill
            Assert.Equal(new Seccomp.Instruction(0x06, 0, 0, 0x7fff0000), program[^1]);  // default: allow
            Assert.True(program.Length < 4096);
            for (int i = 0; i < program.Length; i++)
            {
                if ((program[i].Code & 0x07) == 0x05) // BPF_JMP
                {
                    Assert.InRange(i + 1 + program[i].JumpTrue, 0, program.Length - 1);
                    Assert.InRange(i + 1 + program[i].JumpFalse, 0, program.Length - 1);
                }
            }

            foreach (uint nr in table.Denied.Values)
            {
                int at = Array.FindIndex(program, p => p.Code == 0x15 && p.K == nr && p.JumpFalse == 1);
                Assert.True(at > 0, $"syscall {nr} not denied");
                Assert.Equal(new Seccomp.Instruction(0x06, 0, 0, 0x00050001), program[at + 1]); // EPERM
            }
        }

        Assert.Contains("unshare", Seccomp.X64.Denied.Keys);
        Assert.Equal(Seccomp.X64.Denied.Keys.Except(["iopl", "ioperm", "uselib"]).Order(), Seccomp.Arm64.Denied.Keys.Order());
    }

    /// <summary>A small interpreter for the filter, so the decisions are tested, not just the shape.</summary>
    [Theory]
    [InlineData(272u, 0u, 0u, 0x00050001u)]                 // unshare: EPERM
    [InlineData(56u, 0x01200011u, 0u, 0x7fff0000u)]         // clone for fork (CLONE_CHILD_SETTID|CLEARTID|SIGCHLD): allowed
    [InlineData(56u, 0x10000000u | 0x11u, 0u, 0x00050001u)] // clone with CLONE_NEWUSER: EPERM
    [InlineData(56u, 0x40000000u | 0x11u, 0u, 0x00050001u)] // clone with CLONE_NEWNET: EPERM
    [InlineData(435u, 0u, 0u, 0x00050026u)]                 // clone3: ENOSYS, so libc falls back to clone
    [InlineData(16u, 0u, 0x5412u, 0x00050001u)]             // ioctl TIOCSTI: EPERM
    [InlineData(16u, 0u, 0x5401u, 0x7fff0000u)]             // ioctl TCGETS: allowed
    [InlineData(0u, 0u, 0u, 0x7fff0000u)]                   // read: allowed
    [InlineData(59u, 0u, 0u, 0x7fff0000u)]                  // execve: allowed
    [InlineData(165u, 0u, 0u, 0x00050001u)]                 // mount: EPERM
    [InlineData(101u, 0u, 0u, 0x00050001u)]                 // ptrace: EPERM
    [InlineData(0x40000000u, 0u, 0u, 0x00050001u)]          // x32 ABI read: EPERM
    public void The_x64_filter_decides_as_intended(uint nr, uint arg0, uint arg1, uint expected)
    {
        Assert.Equal(expected, Run(Seccomp.Build(Seccomp.X64), Seccomp.X64.AuditArch, nr, arg0, arg1));
        Assert.Equal(0x80000000u, Run(Seccomp.Build(Seccomp.X64), 0x40000003 /* i386 */, nr, arg0, arg1));
    }

    private static uint Run(Seccomp.Instruction[] program, uint arch, uint nr, uint arg0, uint arg1)
    {
        uint Load(uint offset) => offset switch { 0 => nr, 4 => arch, 16 => arg0, 24 => arg1, _ => throw new InvalidOperationException($"offset {offset}") };
        uint a = 0;
        for (int pc = 0; pc < program.Length;)
        {
            Seccomp.Instruction i = program[pc];
            switch (i.Code)
            {
                case 0x20: a = Load(i.K); pc++; break;
                case 0x15: pc += 1 + (a == i.K ? i.JumpTrue : i.JumpFalse); break;
                case 0x35: pc += 1 + (a >= i.K ? i.JumpTrue : i.JumpFalse); break;
                case 0x45: pc += 1 + ((a & i.K) != 0 ? i.JumpTrue : i.JumpFalse); break;
                case 0x06: return i.K;
                default: throw new InvalidOperationException($"opcode {i.Code:x}");
            }
        }

        throw new InvalidOperationException("fell off the end");
    }

    [Theory]
    [InlineData("/a", "/a", true)]
    [InlineData("/a/b", "/a", true)]
    [InlineData("/ab", "/a", false)]
    [InlineData("/a", "/a/b", false)]
    [InlineData("/anything", "/", true)]
    public void Under_compares_whole_path_components(string path, string root, bool expected)
    {
        Assert.Equal(expected, SandboxPlan.Under(path, root));
    }

    [Fact]
    public void Env_files_are_found_but_directories_and_skipped_trees_are_not()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "envscan-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (string file in (string[])[".env", "a/.env.local", "a/b/c/.env.production", ".git/.env", "node_modules/x/.env", "a/not.env", "a/.environment"])
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, file))!);
                File.WriteAllText(Path.Combine(root, file), "x");
            }

            Directory.CreateDirectory(Path.Combine(root, "venv", ".env", "bin")); // python -m venv .env

            var found = SandboxPlan.EnvFiles(root, recurse: true).Select(f => Path.GetRelativePath(root, f)).Order().ToList();

            Assert.Equal([".env", "a/.env.local", "a/b/c/.env.production"], found);
            Assert.Equal([".env"], SandboxPlan.EnvFiles(root, recurse: false).Select(f => Path.GetRelativePath(root, f)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

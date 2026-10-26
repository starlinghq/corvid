namespace Corvid.Approvals.Tests;

public sealed class HardlineTests
{
    [Theory]
    [InlineData("*", "corvid-vault", true)]
    [InlineData("corvid-v*", "corvid-vault", true)]
    [InlineData("corvid-vaul?", "corvid-vault", true)]
    [InlineData("corvid-[uv]ault", "corvid-vault", true)]
    [InlineData("corvid-[!v]ault", "corvid-vault", false)]
    [InlineData("corvid-[a-z]ault", "corvid-vault", true)]
    [InlineData("*.pem", "server.pem", true)]
    [InlineData("*.pem", "server.pe", false)]
    [InlineData("*", ".local", true)]
    public void Glob_components_match_like_bash_with_dotglob(string pattern, string name, bool matches)
    {
        Assert.Equal(matches, ProtectedPathMatch.Component(pattern, name));
    }

    [Theory]
    [InlineData(".env", true)]
    [InlineData(".env.production", true)]
    [InlineData(".env.example", false)]
    [InlineData(".env.template", false)]
    [InlineData("server.key", true)]
    [InlineData("id_ed25519", true)]
    [InlineData("id_ed25519.pub", false)]
    [InlineData("environment.ts", false)]
    public void Credential_file_names(string name, bool credential)
    {
        Assert.Equal(credential, ProtectedPaths.IsCredentialFileName(name));
    }

    [Fact]
    public void Protected_paths_follow_the_xdg_directories()
    {
        var env = ApprovalEnvironment.Create("/w", "/h", "/data", "/conf", "/run/u");
        // The defaults stay protected when the variables point elsewhere, as the sandbox hides both.
        Assert.Equal(["/h/.local/share/corvid-vault", "/h/.config/corvid-vault", "/data/corvid-vault", "/conf/corvid-vault",
            "/run/u/corvid-vault"], env.Protected.Vault);
        Assert.Equal(["/conf/corvid", "/h/.local/state/corvid/attach"], env.Protected.SafetyConfiguration);
        Assert.Contains("/h/.ssh", env.Protected.CredentialStores);
        Assert.Contains("/conf/gh", env.Protected.CredentialStores);
        Assert.Contains("/run/u", env.Protected.CredentialStores);
        Assert.Equal("/conf/corvid/approvals.json", JsonFileApprovalRuleStore.DefaultPath(env));
    }

    [Fact]
    public void Protected_paths_cover_everything_the_sandbox_hides()
    {
        var variables = new Dictionary<string, string?> { ["XDG_DATA_HOME"] = "/data", ["XDG_CONFIG_HOME"] = "/conf", ["XDG_RUNTIME_DIR"] = "/run/u" };
        var env = ApprovalEnvironment.Create("/w", "/h", "/data", "/conf", "/run/u");
        Assert.Subset(env.Protected.Vault.ToHashSet(), Sandbox.BuiltInDenials.VaultDirectories("/h", variables.GetValueOrDefault).ToHashSet());
        Assert.Subset(env.Protected.CredentialStores.ToHashSet(), Sandbox.BuiltInDenials.SecretPaths("/h", variables.GetValueOrDefault).ToHashSet());
        Assert.Contains(Sandbox.BuiltInDenials.RuntimeDirectory(variables.GetValueOrDefault), env.Protected.CredentialStores);
        Assert.Subset(env.Protected.SafetyConfiguration.ToHashSet(), Sandbox.BuiltInDenials.AttachStateDirectories("/h", variables.GetValueOrDefault).ToHashSet());
    }

    // A6 review H2: attached mode's owner token and paired devices answer approvals over corvid serve --attach. A command
    // that writes them is refused outright, at the default place and under XDG_STATE_HOME.
    [Theory]
    [InlineData("echo x > /h/.local/state/corvid/attach/devices.json")]
    [InlineData("rm -rf /h/.local/state/corvid")]
    [InlineData("cp mine.json /state/corvid/attach/devices.json")]
    [InlineData("echo x > $XDG_STATE_HOME/corvid/attach/owner.token")]
    public void Attached_modes_tokens_are_safety_configuration(string command)
    {
        var env = ApprovalEnvironment.Create("/w", "/h", "/data", "/conf", "/run/u", stateHome: "/state");
        Assert.Contains("/h/.local/state/corvid/attach", env.Protected.SafetyConfiguration);
        Assert.Contains("/state/corvid/attach", env.Protected.SafetyConfiguration);
        Assert.Contains(Hardline.Check(Shell.ShellAnalyzer.Analyze(command, env), env),
            reason => reason.Code == HardlineRules.SafetyConfigurationChange);
    }

    [Theory]
    [InlineData("cat /data/corvid-vault/store.bin")]
    [InlineData("cat /h/.local/share/corvid-vault/store.bin")]
    [InlineData("ls /conf/corvid-vault")]
    [InlineData("cat /h/.config/corvid-vault/vault.json")]
    public void Vault_is_denied_at_its_xdg_and_default_locations(string command)
    {
        var env = ApprovalEnvironment.Create("/w", "/h", "/data", "/conf", "/run/u");
        Assert.Contains(Hardline.Check(Shell.ShellAnalyzer.Analyze(command, env), env), reason => reason.Code == HardlineRules.VaultAccess);
    }

    [Theory]
    [InlineData("echo x > /run/u/bus", HardlineRules.CredentialStoreWrite)]
    [InlineData("cat /run/u/corvid-vault/vault.sock", HardlineRules.VaultAccess)]
    public void The_runtime_directory_is_a_credential_store(string command, string rule)
    {
        var env = ApprovalEnvironment.Create("/w", "/h", "/data", "/conf", "/run/u");
        Assert.Contains(Hardline.Check(Shell.ShellAnalyzer.Analyze(command, env), env), reason => reason.Code == rule);
    }

    [Theory]
    [InlineData("rm -rf build", "")]
    [InlineData("rm -rf /tmp/scratch", HardlineRules.RecursiveDeleteOutsideWorkspace)]
    [InlineData("rm /tmp/scratch/file", "")]
    [InlineData("mkswap /dev/sdb2", HardlineRules.DiskFormat)]
    [InlineData("echo hi > /dev/null", "")]
    [InlineData("echo hi > /dev/tty", "")]
    [InlineData("echo 1 > /dev/sda", HardlineRules.RawDeviceWrite)]
    [InlineData("cp a /dev/shm/a", "")]
    [InlineData("cat ~/.ssh/config", "")]
    [InlineData("vim ~/.ssh/config", "")]
    [InlineData("touch ~/.ssh/config", HardlineRules.CredentialStoreWrite)]
    [InlineData("cat ~/.config/corvid/approvals.json", "")]
    [InlineData("touch ~/.config/corvid/sandbox.json", HardlineRules.SafetyConfigurationChange)]
    [InlineData("echo ok > .env.example", "")]
    [InlineData("ls ~/.local/share", "")]
    [InlineData("echo corvid-vault", "")]
    public void Hardline_rules_by_shape(string command, string rule)
    {
        var reasons = Hardline.Check(Fixtures.Analyze(command), Fixtures.Environment);
        if (rule.Length == 0)
            Assert.Empty(reasons);
        else
            Assert.Contains(reasons, reason => reason.Code == rule);
    }
}

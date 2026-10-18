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
        Assert.Equal(["/data/corvid-vault", "/conf/corvid-vault", "/run/u/corvid-vault"], env.Protected.Vault);
        Assert.Equal(["/conf/corvid"], env.Protected.SafetyConfiguration);
        Assert.Contains("/h/.ssh", env.Protected.CredentialStores);
        Assert.Equal("/conf/corvid/approvals.json", JsonFileApprovalRuleStore.DefaultPath(env));
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

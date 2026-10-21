namespace Corvid.Memory.Tests;

public sealed class ProjectIdsTests
{
    [Theory]
    [InlineData("https://github.com/Example/Widgets.git")]
    [InlineData("https://github.com/example/widgets")]
    [InlineData("https://github.com/example/widgets/")]
    [InlineData("http://github.com/example/widgets.git")]
    [InlineData("git@github.com:example/widgets.git")]
    [InlineData("ssh://git@github.com/example/widgets.git")]
    [InlineData("ssh://git@github.com:22/example/widgets.git")]
    [InlineData("git+ssh://git@github.com/example/widgets")]
    [InlineData("git://github.com/example/widgets.git")]
    [InlineData("https://user:ghp_secrettoken@github.com/example/widgets.git")]
    [InlineData("https://oauth2:p@ss@github.com/example/widgets.git")]
    [InlineData("  https://github.com/example/widgets.git\n")]
    public void Every_form_of_one_remote_has_one_id(string url)
    {
        Assert.Equal("github.com/example/widgets", ProjectIds.NormalizeRemote(url));
        Assert.Equal(ProjectIds.FromRemote("https://github.com/example/widgets"), ProjectIds.FromRemote(url));
    }

    [Fact]
    public void Ids_are_64_lowercase_hex_characters_and_differ_between_repositories()
    {
        var id = ProjectIds.FromRemote("git@github.com:example/widgets.git");
        Assert.True(ProjectIds.IsValid(id));
        Assert.Equal(64, id.Length);
        Assert.Equal(id.ToLowerInvariant(), id);
        Assert.NotEqual(id, ProjectIds.FromRemote("git@github.com:example/gadgets.git"));
        Assert.NotEqual(id, ProjectIds.FromRemote("git@gitlab.com:example/widgets.git"));
        Assert.False(ProjectIds.IsValid("ABC"));
    }

    [Fact]
    public void A_credential_in_the_remote_never_enters_the_key()
    {
        Assert.DoesNotContain("token", ProjectIds.NormalizeRemote("https://me:token123@example.org/team/repo.git"), StringComparison.Ordinal);
        Assert.Equal("example.org/team/repo", ProjectIds.NormalizeRemote("https://me:token123@example.org/team/repo.git"));
    }

    [Fact]
    public void Paths_are_normalized_without_a_trailing_separator()
    {
        var root = Path.Combine(Path.GetTempPath(), "corvid-project");
        Assert.Equal(ProjectIds.FromPath(root), ProjectIds.FromPath(root + "/"));
        Assert.Equal(ProjectIds.FromPath(root), ProjectIds.FromPath(Path.Combine(root, "sub", "..")));
        Assert.NotEqual(ProjectIds.FromPath(root), ProjectIds.FromPath(root + "-other"));
        Assert.Equal(Path.GetFullPath(root).TrimEnd('/').ToLowerInvariant(), ProjectIds.NormalizePath(root));
    }

    [Fact]
    public void A_relative_local_remote_is_resolved_against_its_checkout()
    {
        var a = Path.Combine(Path.GetTempPath(), "a", "tool");
        var b = Path.Combine(Path.GetTempPath(), "b", "other");
        Assert.NotEqual(ProjectIds.FromRemote("../upstream.git", a), ProjectIds.FromRemote("../upstream.git", b));
        Assert.Equal(ProjectIds.FromRemote(Path.Combine(Path.GetTempPath(), "a", "upstream.git")), ProjectIds.FromRemote("../upstream.git", a));
        Assert.Equal(ProjectIds.FromRemote(Path.Combine(a, "x")), ProjectIds.FromRemote("./x", a));
        // Network and absolute remotes are unchanged by the checkout.
        foreach (var url in new[] { "git@github.com:example/widgets.git", "https://github.com/example/widgets", "/srv/git/widgets.git" })
            Assert.Equal(ProjectIds.FromRemote(url), ProjectIds.FromRemote(url, a));
    }
}

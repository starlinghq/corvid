using Corvid.Host.Configuration;

namespace Corvid.Host.Tests;

/// <summary>
/// The Hermes home both imports read (C-035): the expansion C-027 had for memories, plus Hermes' sticky
/// <c>active_profile</c>, checked against hermes-agent at 55ee4acd.
/// </summary>
public sealed class HermesHomeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-hermes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(value => value.Name == name).Value;

    private static string? NoFile(string path) => null;

    [Fact]
    public void The_home_is_the_explicit_one_then_hermes_home_then_the_default()
    {
        Assert.Equal("/x/h", HermesHome.Resolve("/home/u", "/x/h", Env(("HERMES_HOME", "/env/h")), NoFile).Home);
        Assert.Equal("/env/h", HermesHome.Resolve("/home/u", null, Env(("HERMES_HOME", "/env/h")), NoFile).Home);
        Assert.Equal("/env/h", HermesHome.Resolve("/home/u", null, Env(("HERMES_HOME", "  /env/h \n")), NoFile).Home);
        Assert.Equal(Path.Combine("/home/u", ".hermes"), HermesHome.Resolve("/home/u", null, Env(), NoFile).Home);
        Assert.Equal(Path.Combine("/home/u", ".hermes"), HermesHome.Resolve("/home/u", null, Env(("HERMES_HOME", "   ")), NoFile).Home);
    }

    [Fact]
    public void Hermes_home_is_expanded_as_hermes_expands_it_and_an_unusable_one_is_a_problem_not_the_default()
    {
        if (OperatingSystem.IsWindows())
            return;
        HermesHomeLocation With(string value) =>
            HermesHome.Resolve("/home/u", null, Env(("HERMES_HOME", value), ("WORK", "/data/work")), NoFile);

        Assert.Equal(Path.Combine("/home/u", ".hermes-work"), With("~/.hermes-work").Home);
        Assert.Equal("/home/u", With("~").Home);
        Assert.Equal(Path.Combine("/data/work", "hermes"), With("$WORK/hermes").Home);
        Assert.Equal(Path.Combine("/data/work", "hermes"), With("${WORK}/hermes").Home);

        // Relative after expansion (an undefined variable stays as written, as in expandvars): a problem, never ~/.hermes.
        foreach (var value in new[] { "rel", "$UNSET/hermes", "%WORK%/hermes" })
        {
            var location = With(value);
            Assert.Null(location.Home);
            Assert.Contains("not an absolute path", location.Problem, StringComparison.Ordinal);
        }

        // HERMES_DATA_DIR_SUFFIX is appended to the default home, literally.
        Assert.Equal(Path.Combine("/home/u", ".hermes-dev"), HermesHome.Resolve("/home/u", null, Env(("HERMES_DATA_DIR_SUFFIX", "-dev")), NoFile).Home);
    }

    [Fact]
    public void A_sticky_profile_in_the_default_root_selects_its_profile_home()
    {
        var root = Path.Combine(_root, ".hermes");
        var coder = Directory.CreateDirectory(Path.Combine(root, "profiles", "coder")).FullName;
        File.WriteAllText(Path.Combine(root, "active_profile"), "﻿ Coder\n");

        var location = HermesHome.Resolve(_root, environment: Env());
        Assert.Equal((coder, "coder", (string?)null), (location.Home, location.Profile, location.Problem));
        // HERMES_HOME naming the default root reads the same sticky file (systemd sets it so), and so does a home inside it.
        Assert.Equal(coder, HermesHome.Resolve(_root, environment: Env(("HERMES_HOME", root))).Home);
        // An explicit home wins, with no profile applied.
        Assert.Equal((root, (string?)null), (HermesHome.Resolve(_root, root, Env()).Home, HermesHome.Resolve(_root, root, Env()).Profile));
    }

    [Fact]
    public void Default_or_a_blank_sticky_profile_is_the_root()
    {
        var root = Path.Combine(_root, ".hermes");
        Directory.CreateDirectory(root);
        foreach (var content in new[] { "default", "DEFAULT\n", "  \n", "" })
        {
            File.WriteAllText(Path.Combine(root, "active_profile"), content);
            var location = HermesHome.Resolve(_root, environment: Env());
            Assert.Equal((root, (string?)null, (string?)null), (location.Home, location.Profile, location.Problem));
        }
        File.Delete(Path.Combine(root, "active_profile"));
        Assert.Equal(root, HermesHome.Resolve(_root, environment: Env()).Home);
    }

    [Fact]
    public void A_hermes_home_that_is_already_a_profile_is_trusted_without_reading_the_sticky_file()
    {
        var profile = Path.Combine(_root, "custom", "profiles", "writer");
        var reads = new List<string>();
        var location = HermesHome.Resolve(_root, environment: Env(("HERMES_HOME", profile)), readFile: path =>
        {
            reads.Add(path);
            return "coder";
        });
        Assert.Equal((profile, (string?)null, (string?)null), (location.Home, location.Profile, location.Problem));
        Assert.Empty(reads);
    }

    [Fact]
    public void A_custom_root_reads_its_own_sticky_file_and_its_own_profiles()
    {
        var custom = Path.Combine(_root, "opt", "data");
        var work = Directory.CreateDirectory(Path.Combine(custom, "profiles", "work")).FullName;
        var location = HermesHome.Resolve(_root, environment: Env(("HERMES_HOME", custom)),
            readFile: path => path == Path.Combine(custom, "active_profile") ? "work" : null);
        Assert.Equal((work, "work"), (location.Home, location.Profile));
    }

    [Fact]
    public void A_missing_deleted_invalid_or_reserved_sticky_profile_is_a_problem_not_the_root()
    {
        var root = Path.Combine(_root, ".hermes");
        Directory.CreateDirectory(Path.Combine(root, "profiles", "gone"));
        Directory.CreateDirectory(Path.Combine(root, "profiles", ".deleted", "gone"));
        HermesHomeLocation With(string name) => HermesHome.Resolve(_root, environment: Env(), readFile: _ => name);

        var missing = With("coder");
        Assert.Null(missing.Home);
        Assert.Equal("coder", missing.Profile);
        Assert.Contains("no profile at " + Path.Combine(root, "profiles", "coder"), missing.Problem, StringComparison.Ordinal);
        Assert.Contains("no profile at", With("gone").Problem, StringComparison.Ordinal);
        foreach (var invalid in new[] { "../escape", "-dash", "has space", "test", "root", new string('a', 65) })
        {
            var location = With(invalid);
            Assert.Null(location.Home);
            Assert.Contains("not a valid profile name", location.Problem, StringComparison.Ordinal);
        }
    }
}

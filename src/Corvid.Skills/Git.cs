using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;

namespace Corvid.Skills;

/// <summary>Who changed a skill (plan §11): the user, the agent, or a hub it was installed from.</summary>
public sealed record SkillAuthor
{
    private SkillAuthor(string name) => Name = name;

    public static SkillAuthor User { get; } = new("user");

    public static SkillAuthor Agent { get; } = new("agent");

    /// <summary><c>hub:&lt;source&gt;</c>, for example <c>hub:hermes</c>.</summary>
    public static SkillAuthor Hub(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!source.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_' or '.') || source.Length > 64)
            throw new ArgumentException("A hub source is 1–64 lowercase letters, digits, '-', '_' or '.'.", nameof(source));
        return new("hub:" + source);
    }

    /// <summary>The commit author name, as recorded in the skills repository.</summary>
    public string Name { get; }

    public bool IsHub => Name.StartsWith("hub:", StringComparison.Ordinal);

    /// <summary>Written by the user or the agent; anything from a hub is outside content (§10.3).</summary>
    public bool Trusted => !IsHub;

    internal string Email => (IsHub ? "hub" : Name) + "@corvid.invalid";

    /// <summary>The author a commit names, or null for a name Corvid never writes.</summary>
    public static SkillAuthor? FromName(string name) => name switch
    {
        "user" => User,
        "agent" => Agent,
        _ when name.StartsWith("hub:", StringComparison.Ordinal) && name.Length > 4 => TryHub(name[4..]),
        _ => null,
    };

    private static SkillAuthor? TryHub(string source)
    {
        try
        {
            return Hub(source);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public override string ToString() => Name;
}

public sealed record GitResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed class GitException(string message) : InvalidOperationException(message);

/// <summary>
/// Runs the git CLI on the skills repository with nothing of the user's git setup: a cleared environment, no system or
/// global configuration, hooks pointed at nothing, no fsmonitor, pager, external diff, text conversion, signing or
/// credential prompts, and the repository named explicitly so git never searches upwards for another one. The repository's
/// own configuration is checked before use (<see cref="SkillRepository"/>), so nothing there can turn these back on.
/// </summary>
internal sealed class GitCli(string workTree, string executable = "git", TimeSpan? timeout = null)
{
    private static readonly string[] Overrides =
    [
        "core.hooksPath=/dev/null", "core.fsmonitor=false", "core.untrackedCache=false", "core.pager=cat", "core.editor=true",
        "core.askPass=", "core.sshCommand=false", "core.attributesFile=/dev/null", "core.excludesFile=/dev/null",
        "core.symlinks=false", "core.autocrlf=false", "commit.gpgSign=false", "tag.gpgSign=false", "credential.helper=",
        "init.defaultBranch=main", "gc.auto=0", "maintenance.auto=false", "advice.detachedHead=false",
        "protocol.allow=never",
    ];

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);

    public string WorkTree { get; } = Path.GetFullPath(workTree);

    public string Executable { get; } = executable;

    /// <summary>Whether a git executable runs at all.</summary>
    public static bool IsAvailable(string executable = "git")
    {
        try
        {
            return new GitCli(Path.GetTempPath(), executable).RunAsync(["--version"], null, gitDir: false).GetAwaiter().GetResult().Succeeded;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or GitException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <param name="author">Who the commit is by; null for commands that write no commit.</param>
    /// <param name="gitDir">Name the repository explicitly (false only for <c>init</c> and <c>--version</c>).</param>
    public async Task<GitResult> RunAsync(IEnumerable<string> arguments, SkillAuthor? author, bool gitDir = true,
        CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = Directory.Exists(WorkTree) ? WorkTree : Path.GetTempPath(),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        start.Environment["LANG"] = "C.UTF-8";
        start.Environment["LC_ALL"] = "C.UTF-8";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["GIT_EDITOR"] = "true";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_CEILING_DIRECTORIES"] = Path.GetDirectoryName(WorkTree) ?? "/";
        var committer = author ?? SkillAuthor.User;
        start.Environment["GIT_AUTHOR_NAME"] = committer.Name;
        start.Environment["GIT_AUTHOR_EMAIL"] = committer.Email;
        start.Environment["GIT_COMMITTER_NAME"] = "corvid";
        start.Environment["GIT_COMMITTER_EMAIL"] = "corvid@corvid.invalid";
        if (gitDir)
        {
            start.ArgumentList.Add("--git-dir=" + Path.Combine(WorkTree, ".git"));
            start.ArgumentList.Add("--work-tree=" + WorkTree);
        }
        foreach (var setting in Overrides)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(setting);
        }
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("safe.directory=" + WorkTree);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new GitException("git did not start.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new GitException($"git {string.Join(' ', arguments)} timed out.");
        }
        return new GitResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    public async Task<string> CheckedAsync(IEnumerable<string> arguments, SkillAuthor? author = null, CancellationToken cancellationToken = default)
    {
        var list = arguments.ToImmutableArray();
        var result = await RunAsync(list, author, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? result.Output
            : throw new GitException($"git {list.FirstOrDefault()} failed: {(result.Error.Length > 0 ? result.Error : result.Output).Trim()}");
    }
}

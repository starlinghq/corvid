using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Turns;

namespace Corvid.Skills;

/// <summary>One skill eval case (plan §11): a prompt and the tools the agent should call for it, in order.</summary>
public sealed record SkillEvalCase(string Id, string Prompt, ImmutableArray<string> ExpectedTools);

/// <summary>A loaded skill: its document, where it lives, who wrote it and what it carries.</summary>
/// <param name="Folder">Absolute path of the skill folder.</param>
/// <param name="Path">The folder relative to the skills directory, with '/' separators (<c>devops/deploy-k8s</c>).</param>
/// <param name="Author">The author of SKILL.md's last commit; null when it is not committed or names nobody Corvid writes.</param>
/// <param name="AgentOwned">Every file in the folder was last changed by the agent: the only skills the curator and the learning loop may change.</param>
/// <param name="Trusted">No file came from a hub. A body that is not trusted taints the turn that loads it (§10.3).</param>
/// <param name="Resources">L2 files, relative to the folder.</param>
public sealed record Skill(
    SkillDocument Document,
    string Folder,
    string Path,
    SkillAuthor? Author,
    bool AgentOwned,
    bool Trusted,
    ImmutableArray<string> Resources,
    ImmutableArray<SkillEvalCase> Evals)
{
    public string Name => Document.Name;
}

/// <summary>A SKILL.md the loader could not use, and why.</summary>
public sealed record SkillIssue(string Path, string Problem);

public sealed class SkillOwnershipException(string message) : InvalidOperationException(message);

public sealed record SkillStoreOptions
{
    /// <summary>The skills directory, a git repository Corvid creates if needed.</summary>
    public required string Root { get; init; }

    public string GitExecutable { get; init; } = "git";

    /// <summary>Usage, pins and eval results; optional for a store used only to read skills.</summary>
    public SkillsDatabase? Database { get; init; }

    /// <summary>Where <c>SkillChanged</c> events go when a change is made in a session.</summary>
    public IEventLog? Log { get; init; }

    /// <summary>The platform skills are filtered for; default the running one.</summary>
    public string Platform { get; init; } = SkillAvailability.CurrentPlatform;

    /// <summary>Directories nested deeper than this below the root are not searched for skills (Hermes uses one category level).</summary>
    public int MaxDepth { get; init; } = 4;

    /// <summary>An import larger than this, in bytes per skill, is refused.</summary>
    public long MaxImportBytes { get; init; } = 5 * 1024 * 1024;

    public int MaxImportFiles { get; init; } = 500;
}

/// <summary>What an import did, per skill.</summary>
public sealed record ImportResult(ImmutableArray<string> Imported, ImmutableArray<SkillIssue> Refused, string? Revision);

/// <summary>
/// The skills directory (plan §11): agentskills.io <c>SKILL.md</c> folders, found at any depth up to
/// <see cref="SkillStoreOptions.MaxDepth"/> (Hermes groups them in category folders), in a git repository where every change
/// is a commit by <c>user</c>, <c>agent</c> or <c>hub:&lt;source&gt;</c>. It is the kernel's <see cref="ISkillSource"/>:
/// the L0 index after Hermes' activation rules, L1 bodies, and L2 resources. Agent writes go through
/// <see cref="WriteAgentSkillAsync"/>, which refuses any skill not wholly agent-authored.
/// </summary>
/// <remarks>
/// Reading is from a snapshot taken by <see cref="RefreshAsync"/> (at open, after every change Corvid makes, and on
/// <c>/skills</c>), because the turn loop must not wait on git. Edits made on disk are committed as the user's at refresh.
/// </remarks>
public sealed class SkillStore : ISkillSource
{
    public const string ArchiveDirectory = ".archive";

    private readonly SkillStoreOptions _options;
    private volatile Snapshot _snapshot = new(ImmutableDictionary<string, Skill>.Empty, []);

    private sealed record Snapshot(ImmutableDictionary<string, Skill> Skills, ImmutableArray<SkillIssue> Issues);

    private SkillStore(SkillStoreOptions options, SkillRepository repository)
    {
        _options = options;
        Repository = repository;
    }

    public SkillRepository Repository { get; }

    public string Root => Repository.Root;

    public SkillsDatabase? Database => _options.Database;

    public IReadOnlyCollection<Skill> Skills => _snapshot.Skills.Values.OrderBy(skill => skill.Name, StringComparer.Ordinal).ToArray();

    public ImmutableArray<SkillIssue> Issues => _snapshot.Issues;

    public static async Task<SkillStore> OpenAsync(SkillStoreOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var repository = await SkillRepository.OpenAsync(options.Root, options.GitExecutable, cancellationToken).ConfigureAwait(false);
        var store = new SkillStore(options, repository);
        await store.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    public bool TryGet(string name, out Skill skill) => _snapshot.Skills.TryGetValue(name, out skill!);

    /// <summary>Commits edits made on disk as the user's, then reloads every skill and its authorship.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await Repository.RecordOutsideChangesAsync(cancellationToken).ConfigureAwait(false);
        var skills = ImmutableDictionary.CreateBuilder<string, Skill>(StringComparer.Ordinal);
        var issues = ImmutableArray.CreateBuilder<SkillIssue>();
        var authors = await Repository.AuthorsAsync(Root, cancellationToken).ConfigureAwait(false);
        foreach (var folder in FindSkillFolders(Root, _options.MaxDepth))
        {
            var path = Relative(folder);
            try
            {
                var document = SkillDocument.Parse(await File.ReadAllTextAsync(System.IO.Path.Combine(folder, SkillDocument.FileName), cancellationToken).ConfigureAwait(false));
                if (skills.ContainsKey(document.Name))
                {
                    issues.Add(new SkillIssue(path, $"another skill is already named '{document.Name}' ({skills[document.Name].Path})"));
                    continue;
                }
                var files = authors.Where(pair => pair.Key.StartsWith(path + "/", StringComparison.Ordinal)).ToList();
                var owners = files.Select(pair => SkillAuthor.FromName(pair.Value)).ToList();
                var author = authors.TryGetValue(path + "/" + SkillDocument.FileName, out var name) ? SkillAuthor.FromName(name) : null;
                var agentOwned = owners.Count > 0 && owners.All(owner => owner == SkillAuthor.Agent);
                var trusted = owners.Count > 0 && owners.All(owner => owner is { Trusted: true });
                skills[document.Name] = new Skill(document, folder, path, author, agentOwned, trusted, Resources(folder), Evals(folder, issues, path));
            }
            catch (Exception exception) when (exception is SkillFormatException or IOException or UnauthorizedAccessException)
            {
                issues.Add(new SkillIssue(path, exception.Message));
            }
        }
        _snapshot = new Snapshot(skills.ToImmutable(), issues.ToImmutable());
    }

    // ---- ISkillSource ----

    public IReadOnlyList<SkillOption> Available(IReadOnlyCollection<string> tools)
    {
        var registered = tools.ToHashSet(StringComparer.Ordinal);
        return [.. Skills.Where(skill => SkillAvailability.IsAvailable(skill.Document.Activation, registered, _options.Platform, out _))
            .Select(skill => new SkillOption(skill.Name, skill.Document.Description, skill.Document.Examples, skill.Trusted))];
    }

    public SkillBody? Load(string name)
    {
        if (!TryGet(name, out var skill))
            return null;
        try
        {
            _options.Database?.RecordUse(name);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Usage only feeds the curator's staleness; a failure must not keep the skill out of the turn.
        }
        return new SkillBody(skill.Name, skill.Document.Body, skill.Trusted, skill.Resources);
    }

    // ---- L2 ----

    /// <summary>A text file of a skill (L2), confined to its folder: no links, no '..', no binary content.</summary>
    /// <exception cref="ArgumentException">The path is not a readable text file of the skill.</exception>
    public string ReadResource(Skill skill, string relativePath, int maxChars = 64_000)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var full = ResolveInside(skill.Folder, relativePath);
        var bytes = File.ReadAllBytes(full);
        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0)
            throw new ArgumentException($"{relativePath} is a binary file.", nameof(relativePath));
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > maxChars ? text[..maxChars] + "\n… [cut]" : text;
    }

    /// <summary>A file inside <paramref name="folder"/>, refusing absolute paths, '..', hidden entries and links on the way.</summary>
    internal static string ResolveInside(string folder, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (System.IO.Path.IsPathRooted(relativePath))
            throw new ArgumentException("Give a path inside the skill folder.", nameof(relativePath));
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".." || part.StartsWith('.')))
            throw new ArgumentException($"{relativePath} is not a file of the skill.", nameof(relativePath));
        var current = folder;
        foreach (var part in parts)
        {
            current = System.IO.Path.Combine(current, part);
            var info = new FileInfo(current);
            if (info.LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                throw new ArgumentException($"{relativePath} goes through a link, which skills may not follow.", nameof(relativePath));
        }
        if (!File.Exists(current))
            throw new ArgumentException($"{relativePath} is not a file of the skill.", nameof(relativePath));
        return current;
    }

    // ---- writes ----

    /// <summary>
    /// Creates or updates a skill as the agent. An existing skill must be wholly agent-authored (checked after outside
    /// edits are recorded, under the repository's lock): the structural rule that keeps the learning loop and the curator
    /// off skills the user or a hub wrote (#20273, #64338).
    /// </summary>
    /// <returns>The new revision, or null when the content was unchanged.</returns>
    public async Task<string?> WriteAgentSkillAsync(SkillDocument document, string? sessionId = null, string? message = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var existing = TryGet(document.Name, out var skill) ? skill : null;
        var folder = existing?.Folder ?? System.IO.Path.Combine(Root, document.Name);
        if (existing is null && Directory.Exists(folder))
            throw new SkillOwnershipException($"{Relative(folder)} already exists and is not a skill Corvid loaded; the agent does not write into it.");
        var revision = await Repository.CommitAsync(folder, SkillAuthor.Agent, message ?? $"{(existing is null ? "Add" : "Update")} skill {document.Name}",
            _ => File.WriteAllTextAsync(System.IO.Path.Combine(EnsureFolder(folder), SkillDocument.FileName), document.Text, cancellationToken),
            // A new skill's folder is checked again under the lock: one that appeared on disk since the snapshot has just been
            // committed as the user's, and the agent must not write into it.
            check: existing is null ? token => EnsureNewFolderAsync(folder, token) : token => EnsureAgentOwnedAsync(existing, token),
            cancellationToken).ConfigureAwait(false);
        await AfterChangeAsync(document.Name, revision, SkillAuthor.Agent, sessionId, cancellationToken).ConfigureAwait(false);
        return revision;
    }

    /// <summary>Moves an agent-authored skill to <c>.archive/</c> (the curator's stale rule). Pinned skills are never archived.</summary>
    public async Task<string?> ArchiveAsync(string name, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        if (!TryGet(name, out var skill))
            throw new ArgumentException($"There is no skill named '{name}'.", nameof(name));
        if (_options.Database?.Usage(name).Pinned == true)
            throw new SkillOwnershipException($"{name} is pinned; the curator never touches it.");
        var target = System.IO.Path.Combine(Root, ArchiveDirectory, skill.Path.Replace('/', '-') + "-" +
            (_options.Database?.Time ?? TimeProvider.System).GetUtcNow().ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture));
        var revision = await Repository.CommitAsync(Root, SkillAuthor.Agent, $"Archive stale skill {name}",
            _ =>
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                Directory.Move(skill.Folder, target);
                return Task.CompletedTask;
            },
            check: token => EnsureAgentOwnedAsync(skill, token), cancellationToken).ConfigureAwait(false);
        await AfterChangeAsync(name, revision, SkillAuthor.Agent, sessionId, cancellationToken).ConfigureAwait(false);
        return revision;
    }

    /// <summary>Reverts a commit as the user (<c>/skills revert</c>); the reverted files become the user's.</summary>
    public async Task<string> RevertAsync(string revision, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var reverted = await Repository.RevertAsync(revision, SkillAuthor.User, cancellationToken).ConfigureAwait(false);
        await AfterChangeAsync("revert:" + revision, reverted, SkillAuthor.User, sessionId, cancellationToken).ConfigureAwait(false);
        return reverted;
    }

    private async Task EnsureAgentOwnedAsync(Skill skill, CancellationToken cancellationToken)
    {
        var authors = await Repository.AuthorsAsync(skill.Folder, cancellationToken).ConfigureAwait(false);
        var others = authors.Where(pair => pair.Value != SkillAuthor.Agent.Name).ToArray();
        if (authors.Count == 0 || others.Length > 0)
            throw new SkillOwnershipException($"{skill.Name} is not agent-authored ({string.Join(", ", others.Select(pair => $"{pair.Key} by {pair.Value}"))}); " +
                "the agent may change only skills it wrote.");
    }

    private Task EnsureNewFolderAsync(string folder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Directory.Exists(folder) || File.Exists(folder) || new FileInfo(folder).LinkTarget is not null
            ? throw new SkillOwnershipException($"{Relative(folder)} appeared since the skills were loaded and is not the agent's; the agent does not write into it.")
            : Task.CompletedTask;
    }

    private async Task AfterChangeAsync(string skill, string? revision, SkillAuthor author, string? sessionId, CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (revision is null || sessionId is null || _options.Log is not { } log)
            return;
        var events = new[] { EventData.Create(new SkillChanged(skill, revision, author.Name)) };
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                log.Append(sessionId, log.GetHead(sessionId, cancellationToken), events, cancellationToken);
                return;
            }
            catch (EventLogConflictException) when (attempt < 8)
            {
            }
        }
    }

    // ---- import (plan §10.6, D-9) ----

    /// <summary>
    /// Copies the skills under <paramref name="source"/> (one skill folder, or a tree of them such as <c>~/.hermes/skills</c>)
    /// into the store as <paramref name="author"/>, one commit for the lot. Each skill is all or nothing: one with a link, a
    /// special file, a nested repository, an unreadable SKILL.md, a scanner finding, a name already in use or too much
    /// content is refused and reported; the others are imported. Nothing is executed.
    /// </summary>
    public async Task<ImportResult> ImportAsync(string source, SkillAuthor author, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(author);
        if (!author.IsHub)
            throw new ArgumentException("Imports are authored by a hub (hub:<source>); the user's own edits are recorded from disk.", nameof(author));
        var root = System.IO.Path.GetFullPath(source);
        if (!Directory.Exists(root) || new DirectoryInfo(root).LinkTarget is not null)
            throw new ArgumentException($"{source} is not a directory.", nameof(source));
        var imported = ImmutableArray.CreateBuilder<string>();
        var refused = ImmutableArray.CreateBuilder<SkillIssue>();
        var plan = new List<(string Source, string Target, SkillDocument Document, List<string> Files)>();
        var names = _snapshot.Skills.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var folder in FindSkillFolders(root, _options.MaxDepth))
        {
            var relative = System.IO.Path.GetRelativePath(root, folder).Replace('\\', '/');
            var label = relative == "." ? System.IO.Path.GetFileName(root) : relative;
            try
            {
                var files = CheckImportFolder(folder);
                var document = SkillDocument.Parse(await File.ReadAllTextAsync(System.IO.Path.Combine(folder, SkillDocument.FileName), cancellationToken).ConfigureAwait(false));
                if (SkillScanner.ScanFolder(folder) is { IsEmpty: false } findings)
                    throw new InvalidDataException("the scanner flagged it: " + string.Join("; ",
                        findings.Take(5).Select(finding => $"{finding.File}:{finding.Line} {finding.Rule} ({finding.Excerpt})")));
                if (!names.Add(document.Name))
                    throw new InvalidDataException($"a skill named '{document.Name}' already exists");
                var targetRelative = relative == "." ? document.Name : relative;
                var target = System.IO.Path.Combine(Root, targetRelative);
                if (Directory.Exists(target) || File.Exists(target))
                    throw new InvalidDataException($"{targetRelative} already exists in the skills directory");
                plan.Add((folder, target, document, files));
            }
            catch (Exception exception) when (exception is SkillFormatException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                refused.Add(new SkillIssue(label, exception.Message));
            }
        }
        string? revision = null;
        if (plan.Count > 0)
        {
            revision = await Repository.CommitAsync(Root, author, $"Import {plan.Count} skill(s) from {author.Name}", _ =>
            {
                foreach (var (from, target, document, files) in plan)
                {
                    foreach (var file in files)
                    {
                        var destination = System.IO.Path.Combine(target, file);
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
                        File.Copy(System.IO.Path.Combine(from, file), destination, overwrite: false);
                    }
                    imported.Add(document.Name);
                }
                return Task.CompletedTask;
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
            await AfterChangeAsync("import:" + author.Name, revision, author, sessionId, cancellationToken).ConfigureAwait(false);
        }
        return new ImportResult(imported.ToImmutable(), refused.ToImmutable(), revision);
    }

    private List<string> CheckImportFolder(string folder)
    {
        var files = new List<string>();
        long bytes = 0;
        var pending = new Stack<string>([folder]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                var relative = System.IO.Path.GetRelativePath(folder, entry.FullName).Replace('\\', '/');
                if (entry.LinkTarget is not null)
                    throw new InvalidDataException($"{relative} is a link; imported skills may contain only regular files");
                if (entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) || entry.Name.StartsWith(".git", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{relative} is git metadata; imported skills may not carry their own repository or git attributes");
                if (entry.Name.StartsWith('.'))
                    continue;
                if (entry is DirectoryInfo sub)
                {
                    if (File.Exists(System.IO.Path.Combine(sub.FullName, SkillDocument.FileName)))
                        continue;
                    pending.Push(sub.FullName);
                }
                else if (entry is FileInfo file && (file.Attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) == 0 && file.Exists)
                {
                    bytes += file.Length;
                    files.Add(relative);
                }
                else
                {
                    throw new InvalidDataException($"{relative} is not a regular file");
                }
                if (bytes > _options.MaxImportBytes || files.Count > _options.MaxImportFiles)
                    throw new InvalidDataException($"it is larger than {_options.MaxImportBytes / 1024 / 1024} MB or {_options.MaxImportFiles} files");
            }
        }
        return files;
    }

    // ---- scanning ----

    /// <summary>Folders holding a SKILL.md, without following links or entering hidden folders (<c>.git</c>, <c>.archive</c>, Hermes' <c>.hub</c>).</summary>
    internal static IEnumerable<string> FindSkillFolders(string root, int maxDepth)
    {
        var pending = new Queue<(string Path, int Depth)>([(root, 0)]);
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Dequeue();
            var skillFile = new FileInfo(System.IO.Path.Combine(directory, SkillDocument.FileName));
            if (skillFile.Exists && skillFile.LinkTarget is null)
            {
                yield return directory;
                continue;
            }
            if (depth >= maxDepth)
                continue;
            IEnumerable<DirectoryInfo> children;
            try
            {
                children = new DirectoryInfo(directory).EnumerateDirectories().OrderBy(child => child.Name, StringComparer.Ordinal).ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var child in children)
                if (!child.Name.StartsWith('.') && child.LinkTarget is null)
                    pending.Enqueue((child.FullName, depth + 1));
        }
    }

    private static ImmutableArray<string> Resources(string folder)
    {
        var files = new List<string>();
        var pending = new Stack<string>([folder]);
        while (pending.Count > 0 && files.Count < 200)
        {
            foreach (var entry in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos())
            {
                if (entry.Name.StartsWith('.') || entry.LinkTarget is not null)
                    continue;
                if (entry is DirectoryInfo directory)
                    pending.Push(directory.FullName);
                else if (entry.Name != SkillDocument.FileName || System.IO.Path.GetDirectoryName(entry.FullName) != folder)
                    files.Add(System.IO.Path.GetRelativePath(folder, entry.FullName).Replace('\\', '/'));
            }
        }
        return [.. files.Order(StringComparer.Ordinal)];
    }

    /// <summary>Cases in <c>evals/*.json</c>: <c>{"prompt": "...", "expected_tools": ["..."]}</c>. Unknown fields are refused.</summary>
    private static ImmutableArray<SkillEvalCase> Evals(string folder, ImmutableArray<SkillIssue>.Builder issues, string path)
    {
        var directory = System.IO.Path.Combine(folder, "evals");
        if (!Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null)
            return [];
        var cases = ImmutableArray.CreateBuilder<SkillEvalCase>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                if (new FileInfo(file).LinkTarget is not null)
                    continue;
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(property => property.Name is not ("prompt" or "expected_tools"))
                    || !root.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("expected_tools", out var tools) || tools.ValueKind != JsonValueKind.Array
                    || tools.EnumerateArray().Any(tool => tool.ValueKind != JsonValueKind.String))
                    throw new JsonException("an eval case is {\"prompt\": string, \"expected_tools\": [string]}");
                cases.Add(new SkillEvalCase(System.IO.Path.GetFileNameWithoutExtension(file), prompt.GetString()!,
                    [.. tools.EnumerateArray().Select(tool => tool.GetString()!)]));
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                issues.Add(new SkillIssue(path + "/evals/" + System.IO.Path.GetFileName(file), exception.Message));
            }
        }
        return cases.ToImmutable();
    }

    private static string EnsureFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        return folder;
    }

    private string Relative(string folder) => System.IO.Path.GetRelativePath(Root, folder).Replace('\\', '/');
}

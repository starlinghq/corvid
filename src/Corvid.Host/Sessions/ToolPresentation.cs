using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Corvid.Host.Composition;
using Corvid.Tools;
using Corvid.Tools.Builtin;

namespace Corvid.Host.Sessions;

/// <summary>
/// A tool call's kind as ACP names it (<c>tool_call.kind</c>), which every surface draws its card's icon from. The wire
/// names, so a surface that speaks ACP passes them on unchanged.
/// </summary>
public static class ToolCallKinds
{
    public const string Read = "read";
    public const string Edit = "edit";
    public const string Delete = "delete";
    public const string Search = "search";
    public const string Execute = "execute";
    public const string Fetch = "fetch";
    public const string Other = "other";
}

/// <summary>A file edit: the whole file before (null for a new file) and after.</summary>
public sealed record FileDiff(string Path, string? OldText, string NewText);

/// <summary>
/// How a Corvid tool call looks to a surface: its kind, a title, the files it names, and the text of a file before and
/// after an edit (moved here from Corvid.Acp with K-10, so the ACP server and the windowed app show calls alike). Every
/// file read here goes through the file tools' own <see cref="Workspace"/> rules first, so a path the tools may not open
/// (outside the workspace, a protected location, a credential file) is never read or shown.
/// </summary>
/// <param name="runtime">The runtime whose tools are presented.</param>
/// <param name="scope">A6 (G-4): the workspace the calls ran in; the runtime's configured one when null.</param>
public sealed class ToolPresentation(CorvidRuntime runtime, WorkspaceServices? scope = null)
{
    private readonly WorkspaceServices _scope = scope ?? runtime.DefaultWorkspace;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ToolPresentation> _others = new(StringComparer.Ordinal);

    /// <summary>A6 (G-4): the presentation of calls that ran in <paramref name="workspace"/> (this one when it is the same).</summary>
    public ToolPresentation In(WorkspaceServices workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace.Root == _scope.Root ? this : _others.GetOrAdd(workspace.Root, _ => new ToolPresentation(runtime, workspace));
    }

    /// <summary>A6: the presentation of a session's calls, in the workspace it works in.</summary>
    public ToolPresentation ForSession(string sessionId) => In(runtime.WorkspaceServicesOf(sessionId));

    /// <summary>
    /// As <see cref="ForSession"/>, or this presentation when the session's workspace cannot be used now (a removed
    /// worktree): its calls are still listed, and paths outside this workspace are not read.
    /// </summary>
    internal ToolPresentation ForSessionOrSelf(string sessionId)
    {
        try
        {
            return ForSession(sessionId);
        }
        catch (WorkspaceUnavailableException)
        {
            return this;
        }
    }

    /// <summary>A surface shows a diff of the whole file; past this size, or for binary content, the call reports text only.</summary>
    public const int MaxDiffBytes = 256 * 1024;
    private const int MaxTitle = 160;
    private const int MaxResultText = 8 * 1024;
    private const int MaxDiffFiles = 16;

    private readonly Workspace _workspace = Workspace.For((scope ?? runtime.DefaultWorkspace).Environment);

    /// <summary>The workspace the tools work in, as an absolute path.</summary>
    public string WorkspaceRoot => _workspace.Root;

    public string Kind(string tool)
    {
        if (!_scope.Tools.TryGet(tool, out var function))
            return ToolCallKinds.Other;
        var risk = function.Descriptor.Risk;
        return tool switch
        {
            "shell" or "skill_run_script" => ToolCallKinds.Execute,
            "search_files" or "memory_search" => ToolCallKinds.Search,
            _ when risk.Network => ToolCallKinds.Fetch,
            _ when risk.Destructive && risk.SideEffects.HasFlag(SideEffects.Filesystem) && tool.Contains("delete", StringComparison.Ordinal) => ToolCallKinds.Delete,
            _ when risk.SideEffects.HasFlag(SideEffects.Filesystem) => ToolCallKinds.Edit,
            _ when risk.ReadOnly => ToolCallKinds.Read,
            _ => ToolCallKinds.Other,
        };
    }

    /// <summary>The tool reads the network: its result is outside content, which taints the session (plan §10.3).</summary>
    public bool Network(string tool) => _scope.Tools.TryGet(tool, out var function) && function.Descriptor.Risk.Network;

    /// <summary>What a call acts on, in a line: its command line, path, URL, query or name; null when it names none.</summary>
    public static string? Target(JsonElement? arguments) =>
        arguments is { ValueKind: JsonValueKind.Object } args
            ? new[] { "command", "path", "url", "query", "name" }
                .Select(name => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.ReplaceLineEndings(" ")
            : null;

    /// <summary>The tool's name and what it acts on: its command line, path or URL when it has one.</summary>
    public static string Title(string tool, JsonElement? arguments)
    {
        var target = Target(arguments);
        var title = target is null ? tool : $"{tool}: {target}";
        return title.Length <= MaxTitle ? title : title[..(MaxTitle - 1)] + "…";
    }

    /// <summary>The file a call names, as an absolute path the tools may open; null when it names none or one they refuse.</summary>
    public string? Path(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } args || !args.TryGetProperty("path", out var value)
            || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } path)
            return null;
        try
        {
            return _workspace.Resolve(path);
        }
        catch (ToolArgumentException)
        {
            return null;
        }
    }

    /// <summary>The files a call names, as absolute paths; empty when it names none the tools may open.</summary>
    public ImmutableArray<string> Locations(JsonElement? arguments) => Path(arguments) is { } path ? [path] : [];

    /// <summary>
    /// The edit a pending <c>write_file</c> or <c>patch_file</c> call would make, for the permission prompt: the file as it is
    /// now and as the call would leave it. Null when it cannot be shown exactly.
    /// </summary>
    public FileDiff? ProposedDiff(string tool, JsonElement? arguments)
    {
        if (tool is not ("write_file" or "patch_file") || Path(arguments) is not { } path || arguments is not { } args)
            return null;
        var current = ReadText(path, out var exists);
        if (exists && current is null)
            return null;
        if (tool == "write_file")
            return String(args, "content") is { } content ? new FileDiff(path, current, content) : null;
        if (current is null || String(args, "old_text") is not { Length: > 0 } old || String(args, "new_text") is not { } replacement)
            return null;
        var all = args.TryGetProperty("replace_all", out var flag) && flag.ValueKind == JsonValueKind.True;
        // The tool replaces text that occurs exactly once unless told to replace every occurrence; anything else fails, so no diff.
        var first = current.IndexOf(old, StringComparison.Ordinal);
        if (first < 0 || !all && current.IndexOf(old, first + old.Length, StringComparison.Ordinal) >= 0)
            return null;
        return new FileDiff(path, current, all ? current.Replace(old, replacement, StringComparison.Ordinal)
            : string.Concat(current.AsSpan(0, first), replacement, current.AsSpan(first + old.Length)));
    }

    /// <summary>
    /// What a finished call changed, from its write ledger (C-014): each path its after-checkpoint lists, as it was in the
    /// before-checkpoint and as it is in the after one. Empty without checkpoints.
    /// </summary>
    public async Task<IReadOnlyList<FileDiff>> LedgerDiffsAsync(string beforeRevision, string afterRevision, IReadOnlyList<string> paths)
    {
        if (_scope.Checkpoints is not { Store: { IsAvailable: true } store })
            return [];
        var diffs = new List<FileDiff>();
        foreach (var relative in paths.Take(MaxDiffFiles))
        {
            string absolute;
            try
            {
                absolute = _workspace.Resolve(relative);
            }
            catch (ToolArgumentException)
            {
                continue;
            }
            var key = store.Relative(absolute);
            if (key is null)
                continue;
            var before = await store.ReadAsync(beforeRevision, [absolute]).ConfigureAwait(false);
            var after = await store.ReadAsync(afterRevision, [absolute]).ConfigureAwait(false);
            var oldText = before.TryGetValue(key, out var oldFile) && !oldFile.IsLink ? Decode(await store.ReadBlobAsync(oldFile.Blob).ConfigureAwait(false)) : null;
            var newText = after.TryGetValue(key, out var newFile) && !newFile.IsLink ? Decode(await store.ReadBlobAsync(newFile.Blob).ConfigureAwait(false)) : null;
            // A binary or oversized side means no faithful diff; a path absent before is a new file, absent after a deletion.
            if (oldFile.Blob is not null && oldText is null || newFile.Blob is not null && newText is null)
                continue;
            diffs.Add(new FileDiff(absolute, oldText, newText ?? ""));
        }
        return diffs;
    }

    /// <summary>A tool result as text for a surface: strings as they are, anything else as JSON, cut to a readable size.</summary>
    public static string ResultText(JsonElement result)
    {
        var text = result.ValueKind == JsonValueKind.String ? result.GetString() ?? "" : result.GetRawText();
        return text.Length <= MaxResultText ? text : text[..MaxResultText] + "…";
    }

    private string? ReadText(string path, out bool exists)
    {
        exists = File.Exists(path);
        if (!exists)
            return null;
        try
        {
            var info = new FileInfo(path);
            return info.Length > MaxDiffBytes ? null : Decode(File.ReadAllBytes(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Decode(byte[] bytes)
    {
        if (bytes.Length > MaxDiffBytes || bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).IndexOf((byte)0) >= 0)
            return null;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static string? String(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

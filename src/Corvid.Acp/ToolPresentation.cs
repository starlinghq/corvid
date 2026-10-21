using System.Text;
using System.Text.Json;
using Corvid.Host.Composition;
using Corvid.Tools;
using Corvid.Tools.Builtin;

namespace Corvid.Acp;

/// <summary>
/// How a Corvid tool call looks to an editor: its ACP kind, a title, the files it names, and the text of a file before and
/// after an edit. Every file read here goes through the file tools' own <see cref="Workspace"/> rules first, so a path the
/// tools may not open (outside the workspace, a protected location, a credential file) is never read or shown.
/// </summary>
internal sealed class ToolPresentation(CorvidRuntime runtime)
{
    // An editor shows a diff of the whole file; past this size, or for binary content, the call reports text only.
    internal const int MaxDiffBytes = 256 * 1024;
    private const int MaxTitle = 160;
    private const int MaxResultText = 8 * 1024;

    private readonly Workspace _workspace = Workspace.For(runtime.Builtin.Environment);

    public string Kind(string tool)
    {
        if (!runtime.Tools.TryGet(tool, out var function))
            return ToolKinds.Other;
        var risk = function.Descriptor.Risk;
        return tool switch
        {
            "shell" or "skill_run_script" => ToolKinds.Execute,
            "search_files" or "memory_search" => ToolKinds.Search,
            _ when risk.Network => ToolKinds.Fetch,
            _ when risk.Destructive && risk.SideEffects.HasFlag(SideEffects.Filesystem) && tool.Contains("delete", StringComparison.Ordinal) => ToolKinds.Delete,
            _ when risk.SideEffects.HasFlag(SideEffects.Filesystem) => ToolKinds.Edit,
            _ when risk.ReadOnly => ToolKinds.Read,
            _ => ToolKinds.Other,
        };
    }

    /// <summary>The tool's name and what it acts on: its command line, path or URL when it has one.</summary>
    public static string Title(string tool, JsonElement? arguments)
    {
        var target = arguments is { ValueKind: JsonValueKind.Object } args
            ? new[] { "command", "path", "url", "query", "name" }
                .Select(name => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            : null;
        var title = target is null ? tool : $"{tool}: {target.ReplaceLineEndings(" ")}";
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

    public ToolCallLocation[]? Locations(JsonElement? arguments) => Path(arguments) is { } path ? [new ToolCallLocation(path)] : null;

    /// <summary>
    /// The edit a pending <c>write_file</c> or <c>patch_file</c> call would make, for the permission prompt: the file as it is
    /// now and as the call would leave it. Null when it cannot be shown exactly.
    /// </summary>
    public DiffToolCallContent? ProposedDiff(string tool, JsonElement? arguments)
    {
        if (tool is not ("write_file" or "patch_file") || Path(arguments) is not { } path || arguments is not { } args)
            return null;
        var current = ReadText(path, out var exists);
        if (exists && current is null)
            return null;
        if (tool == "write_file")
            return String(args, "content") is { } content ? new DiffToolCallContent(path, current, content) : null;
        if (current is null || String(args, "old_text") is not { Length: > 0 } old || String(args, "new_text") is not { } replacement)
            return null;
        var all = args.TryGetProperty("replace_all", out var flag) && flag.ValueKind == JsonValueKind.True;
        // The tool replaces text that occurs exactly once unless told to replace every occurrence; anything else fails, so no diff.
        var first = current.IndexOf(old, StringComparison.Ordinal);
        if (first < 0 || !all && current.IndexOf(old, first + old.Length, StringComparison.Ordinal) >= 0)
            return null;
        return new DiffToolCallContent(path, current, all ? current.Replace(old, replacement, StringComparison.Ordinal)
            : string.Concat(current.AsSpan(0, first), replacement, current.AsSpan(first + old.Length)));
    }

    /// <summary>
    /// What a finished call changed, from its write ledger (C-014): each path its after-checkpoint lists, as it was in the
    /// before-checkpoint and as it is in the after one. Empty without checkpoints.
    /// </summary>
    public async Task<IReadOnlyList<DiffToolCallContent>> LedgerDiffsAsync(string beforeRevision, string afterRevision,
        IReadOnlyList<string> paths)
    {
        if (runtime.Checkpoints is not { Store: { IsAvailable: true } store })
            return [];
        var diffs = new List<DiffToolCallContent>();
        foreach (var relative in paths.Take(16))
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
            diffs.Add(new DiffToolCallContent(absolute, oldText, newText ?? ""));
        }
        return diffs;
    }

    /// <summary>A tool result as text for the editor: strings as they are, anything else as JSON, cut to a readable size.</summary>
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

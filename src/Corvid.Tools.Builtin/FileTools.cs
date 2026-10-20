using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Corvid.Tools.Builtin;

/// <summary>Limits for the file tools. Defaults keep a result within a small local model's context.</summary>
public sealed record FileToolOptions
{
    public static FileToolOptions Default { get; } = new();

    /// <summary>The most characters one <c>read_file</c> call returns; ask again with a later start line for more.</summary>
    public int MaxReadChars { get; init; } = 24_000;

    /// <summary>Files larger than this are not read or searched.</summary>
    public long MaxFileBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>The largest file <c>write_file</c> or <c>patch_file</c> writes.</summary>
    public int MaxWriteBytes { get; init; } = 1024 * 1024;

    public int MaxListEntries { get; init; } = 200;

    public int MaxSearchResults { get; init; } = 50;

    /// <summary>Files a search may open before it stops and reports truncation.</summary>
    public int MaxSearchFiles { get; init; } = 5_000;

    /// <summary>Directory names a recursive listing or a search does not descend into.</summary>
    public IReadOnlySet<string> SkippedDirectories { get; init; } = new HashSet<string>(StringComparer.Ordinal) { ".git", "node_modules" };
}

/// <summary>
/// Read, list, search, write and patch files in the workspace (plan §7, §10.1). Every path is resolved by
/// <see cref="Workspace"/>, so nothing outside the workspace, nothing protected and no credential file is reached, links
/// included. Writes are side effects, so the turn loop sends them through the approval pipeline first; their path
/// parameter is named <c>path</c>, which is how C-007b finds the target.
/// </summary>
public sealed partial class FileTools(Workspace workspace, FileToolOptions? options = null)
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private readonly FileToolOptions _options = options ?? FileToolOptions.Default;

    public Workspace Workspace { get; } = workspace ?? throw new ArgumentNullException(nameof(workspace));

    [Tool("read_file", Description = "Read a text file in the workspace, with line numbers. Long files come in parts: pass start_line to continue.")]
    public JsonNode ReadFile(
        [Description("The file, relative to the workspace.")] string path,
        [Description("The first line to return, counting from 1.")] int start_line = 1,
        [Description("The most lines to return.")] int max_lines = 400)
    {
        if (start_line < 1)
            throw new ToolArgumentException("Argument 'start_line' must be 1 or more.", nameof(start_line));
        if (max_lines < 1)
            throw new ToolArgumentException("Argument 'max_lines' must be 1 or more.", nameof(max_lines));
        var file = Existing(path, directory: false);
        var text = ReadText(file, path);
        var lines = text.Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0 && text.Length > 0)
            lines = lines[..^1];
        var shown = new StringBuilder();
        var line = start_line;
        for (; line <= lines.Length && line < start_line + max_lines; line++)
        {
            var content = lines[line - 1].TrimEnd('\r');
            if (shown.Length + content.Length > _options.MaxReadChars && shown.Length > 0)
                break;
            shown.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(content).Append('\n');
        }

        var result = new JsonObject
        {
            ["path"] = Workspace.Relative(file),
            ["total_lines"] = lines.Length,
            ["start_line"] = start_line,
            ["end_line"] = line - 1,
            ["content"] = shown.ToString(),
        };
        if (line <= lines.Length)
            result["next_start_line"] = line;
        return result;
    }

    [Tool("list_files", Description = "List a directory in the workspace: subdirectories end with '/', files show their size.")]
    public JsonNode ListFiles(
        [Description("The directory, relative to the workspace; '.' is the workspace itself.")] string path = ".",
        [Description("Also list everything below it.")] bool recursive = false)
    {
        var directory = Existing(path, directory: true);
        var entries = new JsonArray();
        var truncated = false;
        foreach (var entry in Walk(directory, recursive))
        {
            if (entries.Count == _options.MaxListEntries)
            {
                truncated = true;
                break;
            }

            var relative = Workspace.Relative(entry.FullName);
            entries.Add((JsonNode?)JsonValue.Create(entry is DirectoryInfo ? relative + "/" : $"{relative} ({Size(((FileInfo)entry).Length)})"));
        }

        return new JsonObject { ["path"] = Workspace.Relative(directory), ["entries"] = entries, ["truncated"] = truncated };
    }

    [Tool("search_files", Description = "Search the text files in the workspace for a phrase (or a regular expression) and return the matching lines.")]
    public JsonNode SearchFiles(
        [Description("The text to look for.")] string pattern,
        [Description("The directory or file to search, relative to the workspace.")] string path = ".",
        [Description("Treat the pattern as a .NET regular expression instead of plain text.")] bool regex = false,
        [Description("Match upper and lower case alike.")] bool ignore_case = true)
    {
        if (string.IsNullOrEmpty(pattern))
            throw new ToolArgumentException("Argument 'pattern' must not be empty.", nameof(pattern));
        Regex matcher;
        try
        {
            // Non-backtracking: a pattern from the model cannot take exponential time.
            matcher = new Regex(regex ? pattern : Regex.Escape(pattern),
                RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | (ignore_case ? RegexOptions.IgnoreCase : RegexOptions.None));
        }
        catch (ArgumentException exception)
        {
            throw new ToolArgumentException($"Argument 'pattern' is not a valid regular expression: {exception.Message}", nameof(pattern));
        }

        var root = Workspace.Resolve(path);
        IEnumerable<FileSystemInfo> files = File.Exists(root) ? [new FileInfo(root)]
            : Directory.Exists(root) ? Walk(root, recursive: true)
            : throw new ToolArgumentException($"Argument 'path' is '{path}', which does not exist.", nameof(path));
        var matches = new JsonArray();
        var opened = 0;
        var truncated = false;
        foreach (var file in files.OfType<FileInfo>())
        {
            if (file.Length > _options.MaxFileBytes || file.LinkTarget is not null)
                continue;
            if (++opened > _options.MaxSearchFiles || matches.Count >= _options.MaxSearchResults)
            {
                truncated = true;
                break;
            }

            var text = TryReadText(file.FullName);
            if (text is null)
                continue;
            var number = 0;
            foreach (var line in text.Split('\n'))
            {
                number++;
                if (!matcher.IsMatch(line))
                    continue;
                if (matches.Count == _options.MaxSearchResults)
                {
                    truncated = true;
                    break;
                }

                var shown = line.TrimEnd('\r');
                matches.Add((JsonNode?)JsonValue.Create($"{Workspace.Relative(file.FullName)}:{number}: {(shown.Length > 300 ? shown[..300] + "…" : shown)}"));
            }
        }

        return new JsonObject { ["pattern"] = pattern, ["matches"] = matches, ["truncated"] = truncated };
    }

    [Tool("write_file", Description = "Create or replace a text file in the workspace with the given content. Missing directories are created.")]
    [SideEffect(SideEffects.Filesystem)]
    // Replacing a file loses its old content, and nothing can restore it until checkpoints exist (C-014).
    [Destructive]
    public JsonNode WriteFile(
        [Description("The file, relative to the workspace.")] string path,
        [Description("The complete new content of the file.")] string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var file = Workspace.Resolve(path);
        if (Directory.Exists(file))
            throw new ToolArgumentException($"Argument 'path' is '{path}', which is a directory.", nameof(path));
        var bytes = Utf8.GetBytes(content);
        if (bytes.Length > _options.MaxWriteBytes)
            throw new ToolArgumentException($"The content is {Size(bytes.Length)}, over the {Size(_options.MaxWriteBytes)} limit.", nameof(content));
        var created = !File.Exists(file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        // Re-check after creating directories: a directory in the path may have been a link created meanwhile.
        if (Workspace.Resolve(path) != file)
            throw new ToolArgumentException($"Argument 'path' is '{path}', which changed while it was being written.", nameof(path));
        WriteAtomically(file, bytes);
        return new JsonObject { ["path"] = Workspace.Relative(file), ["bytes"] = bytes.Length, ["created"] = created };
    }

    [Tool("patch_file", Description = "Edit a text file in the workspace by replacing an exact piece of its text with new text. The old text must appear exactly once unless replace_all is true.")]
    [SideEffect(SideEffects.Filesystem)]
    public JsonNode PatchFile(
        [Description("The file, relative to the workspace.")] string path,
        [Description("The exact text to replace, including whitespace.")] string old_text,
        [Description("The text to put in its place.")] string new_text,
        [Description("Replace every occurrence instead of exactly one.")] bool replace_all = false)
    {
        if (string.IsNullOrEmpty(old_text))
            throw new ToolArgumentException("Argument 'old_text' must not be empty; use write_file to create a file.", nameof(old_text));
        ArgumentNullException.ThrowIfNull(new_text);
        var file = Existing(path, directory: false);
        var text = ReadText(file, path);
        // Files with CRLF line endings: match the model's LF text against them too.
        var crlf = !text.Contains(old_text, StringComparison.Ordinal) && text.Contains("\r\n", StringComparison.Ordinal);
        var find = crlf ? old_text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) : old_text;
        var replacement = crlf ? new_text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) : new_text;
        var count = Count(text, find);
        if (count == 0)
            throw new ToolArgumentException($"'old_text' does not appear in {path}. Read the file and copy the text exactly.", nameof(old_text));
        if (count > 1 && !replace_all)
            throw new ToolArgumentException($"'old_text' appears {count} times in {path}. Include more surrounding text to pick one, or set replace_all.", nameof(old_text));
        var updated = replace_all ? text.Replace(find, replacement, StringComparison.Ordinal) : ReplaceFirst(text, find, replacement);
        var bytes = Utf8.GetBytes(updated);
        if (bytes.Length > _options.MaxWriteBytes)
            throw new ToolArgumentException($"The patched file would be {Size(bytes.Length)}, over the {Size(_options.MaxWriteBytes)} limit.", nameof(new_text));
        WriteAtomically(file, bytes);
        return new JsonObject { ["path"] = Workspace.Relative(file), ["replacements"] = replace_all ? count : 1 };
    }

    private string Existing(string path, bool directory)
    {
        var resolved = Workspace.Resolve(path);
        if (directory ? Directory.Exists(resolved) : File.Exists(resolved))
            return resolved;
        if (directory ? File.Exists(resolved) : Directory.Exists(resolved))
            throw new ToolArgumentException($"Argument 'path' is '{path}', which is a {(directory ? "file" : "directory")}.", nameof(path));
        throw new ToolArgumentException($"Argument 'path' is '{path}', which does not exist.", nameof(path));
    }

    private string ReadText(string file, string path)
    {
        var length = new FileInfo(file).Length;
        if (length > _options.MaxFileBytes)
            throw new ToolArgumentException($"'{path}' is {Size(length)}, over the {Size(_options.MaxFileBytes)} limit.", nameof(path));
        return TryReadText(file) ?? throw new ToolArgumentException($"'{path}' is a binary file ({Size(length)}), not text.", nameof(path));
    }

    /// <summary>The file as UTF-8, or null when it looks binary (a NUL byte in its first 8 KiB).</summary>
    private static string? TryReadText(string file)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0) ? null : Utf8.GetString(bytes);
    }

    /// <summary>Entries in name order, directories before descending into them; skipped directories, links and secrets are left out.</summary>
    private IEnumerable<FileSystemInfo> Walk(string directory, bool recursive)
    {
        var pending = new Stack<string>([directory]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            FileSystemInfo[] children;
            try
            {
                children = new DirectoryInfo(current).GetFileSystemInfos();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var descend = new List<string>();
            foreach (var child in children.OrderBy(child => child.Name, StringComparer.Ordinal))
            {
                if (!Workspace.Visible(child.FullName))
                    continue;
                // Links are listed but never followed: one could lead out of the workspace or into a loop.
                if (child is DirectoryInfo && child.LinkTarget is null)
                {
                    if (_options.SkippedDirectories.Contains(child.Name))
                        continue;
                    if (recursive)
                        descend.Add(child.FullName);
                }

                yield return child;
            }

            for (var i = descend.Count - 1; i >= 0; i--)
                pending.Push(descend[i]);
        }
    }

    private static void WriteAtomically(string file, byte[] bytes)
    {
        var temp = Path.Combine(Path.GetDirectoryName(file)!, $".{Path.GetFileName(file)}.corvid-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(file) && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, File.GetUnixFileMode(file));
            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static int Count(string text, string find)
    {
        var count = 0;
        for (var index = text.IndexOf(find, StringComparison.Ordinal); index >= 0; index = text.IndexOf(find, index + find.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string ReplaceFirst(string text, string find, string replacement)
    {
        var index = text.IndexOf(find, StringComparison.Ordinal);
        return string.Concat(text.AsSpan(0, index), replacement, text.AsSpan(index + find.Length));
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
    };
}

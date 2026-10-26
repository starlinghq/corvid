using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Corvid.Abstractions.Media;
using Corvid.Approvals;
using Corvid.Models.Images;
using Corvid.Tools;
using Corvid.Tools.Builtin;

namespace Corvid.Host.Sessions;

/// <summary>
/// K-8 (A6): images a person names from a surface, read the same way by the CLI and the app. <c>@path</c> in a message names
/// an image file in the workspace (resolved by the file tools' <see cref="Workspace"/> rules: inside it after links, no
/// protected location, no credential file); the message keeps its text, so the model reads the reference beside the
/// image. <c>/image &lt;path&gt;</c> (the CLI) and a dropped or attached file name any file the person can read, still never
/// a credential file or a protected location (Vault's, credential stores, Corvid's safety configuration). Every file is
/// size-checked before it is read; its format is the kernel's to check, from the bytes.
/// </summary>
public static partial class ImageAttachments
{
    /// <summary>
    /// The image paths a message names with <c>@</c>: <c>@shot.png</c>, <c>@docs/diagram.webp</c>, or quoted for a path with
    /// spaces, <c>@"screen shot.png"</c>. Only names with an image extension count; other <c>@</c> words are left alone.
    /// </summary>
    public static ImmutableArray<string> References(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var paths = new List<string>();
        foreach (Match match in Reference().Matches(text))
        {
            var path = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["bare"].Value.TrimEnd('.', ',', ';', ':', ')', '!', '?');
            if (path.Length > 0 && ImageFiles.HasImageExtension(path) && !paths.Contains(path, StringComparer.Ordinal))
                paths.Add(path);
        }
        return [.. paths];
    }

    /// <summary>
    /// The images a message's <c>@</c> references name, read from the workspace; each problem (outside the workspace, a
    /// credential file, missing, too large) is a line for the person and that image is left out.
    /// </summary>
    public static (ImmutableArray<ImageInput> Images, ImmutableArray<string> Problems) FromReferences(string text, ApprovalEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var references = References(text);
        if (references.IsEmpty)
            return ([], []);
        var workspace = Workspace.For(environment);
        var images = ImmutableArray.CreateBuilder<ImageInput>();
        var problems = ImmutableArray.CreateBuilder<string>();
        foreach (var reference in references)
        {
            try
            {
                images.Add(Read(workspace.Resolve(reference)));
            }
            catch (Exception exception) when (exception is ToolArgumentException or IOException or UnauthorizedAccessException or ImageRefusedException)
            {
                problems.Add($"@{reference}: {Reason(exception)}");
            }
        }
        return (images.ToImmutable(), problems.ToImmutable());
    }

    /// <summary>
    /// An image file the person named (<c>/image</c>, a drop, the attach box): any readable file but a credential file or one
    /// in a protected location; <c>~</c> is the home directory and a relative path is the workspace's.
    /// </summary>
    /// <exception cref="ImageRefusedException">Why it was not read, for the person.</exception>
    public static ImageInput FromPath(string path, ApprovalEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(environment);
        var trimmed = path.Trim().Trim('"');
        var expanded = trimmed == "~" || trimmed.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(environment.Home, trimmed.Length > 1 ? trimmed[2..] : "")
            : trimmed;
        var full = Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(environment.Workspace, expanded));
        try
        {
            var real = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
            var protectedPaths = environment.Protected;
            foreach (var denied in protectedPaths.Vault.Concat(protectedPaths.CredentialStores).Concat(protectedPaths.SafetyConfiguration))
                if (Path.IsPathFullyQualified(denied) && (Under(full, denied) || Under(real, denied)))
                    throw new ImageRefusedException($"{trimmed} is inside a protected location ({denied}); Corvid does not read files there.");
            if (Workspace.IsSecretName(Path.GetFileName(real)) || Workspace.IsSecretName(Path.GetFileName(full)))
                throw new ImageRefusedException($"{trimmed} is a credential file; Corvid does not read those.");
            return Read(real, Path.GetFileName(full));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ImageRefusedException($"{trimmed}: {Reason(exception)}");
        }
    }

    // The size is checked before reading, so a huge file named by mistake is never loaded.
    private static ImageInput Read(string path, string? name = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("there is no such file.", path);
        if (info.Length > ImageFiles.MaxBytes)
            throw new ImageRefusedException($"it is {ImageFiles.Size(info.Length)}; an image may be at most {ImageFiles.Size(ImageFiles.MaxBytes)}.");
        return new ImageInput(File.ReadAllBytes(path), name ?? info.Name);
    }

    private static string Reason(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException => "there is no such file.",
        UnauthorizedAccessException => "it cannot be read (permission denied).",
        _ => exception.Message,
    };

    private static bool Under(string path, string root)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path == normalized || path.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    // "@" at the start or after whitespace or an opening bracket, then a quoted path or a run of non-space characters.
    [GeneratedRegex("""(?<=^|[\s(\[])@(?:"(?<quoted>[^"\r\n]+)"|(?<bare>[^\s"]+))""", RegexOptions.CultureInvariant)]
    private static partial Regex Reference();
}

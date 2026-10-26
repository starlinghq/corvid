using System.Globalization;
using Corvid.Abstractions.Media;
using Corvid.Approvals;
using Corvid.Host.Commands;
using Corvid.Host.Sessions;
using Corvid.Models.Images;
using Spectre.Console;

namespace Corvid.Cli.Chat;

/// <summary>
/// K-8 (A6): <c>/image &lt;path&gt;</c> in <c>corvid chat</c> stages an image for the next message; <c>/image</c> lists what
/// is staged and <c>/image clear</c> drops it. <c>@path</c> in a message attaches a workspace image without staging. The
/// chat's own command, not the shared registry's: the app attaches by drop, paste or its attach box, and ACP by image
/// blocks. Staged images go with the next message only, and only the person's own (a <c>!</c> line keeps them staged).
/// </summary>
internal static class ImageCommand
{
    public const string Usage = "/image <path> stages an image for your next message; /image lists them; /image clear drops them.";

    public static bool Matches(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Equals("/image", StringComparison.Ordinal) || trimmed.StartsWith("/image ", StringComparison.Ordinal);
    }

    /// <summary>Runs the command and returns its reply, as markup.</summary>
    public static string Run(string line, List<ImageInput> staged, ApprovalEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(staged);
        var argument = line.TrimStart()["/image".Length..].Trim();
        if (argument.Length == 0)
            return staged.Count == 0
                ? $"[grey]No image is staged. {Markup.Escape(Usage)}[/]"
                : $"[grey]Staged for your next message: {Markup.Escape(List(staged))}.[/]";
        if (argument == "clear")
        {
            var count = staged.Count;
            staged.Clear();
            return $"[grey]{(count == 0 ? "No image was staged." : Plural(count, "staged image") + " dropped.")}[/]";
        }
        if (staged.Count >= ImageFiles.MaxPerMessage)
            return $"[yellow]A message may carry at most {ImageFiles.MaxPerMessage} images; {staged.Count} are staged.[/]";
        ImageInput image;
        try
        {
            image = ImageAttachments.FromPath(argument, environment);
        }
        catch (ImageRefusedException refused)
        {
            return $"[yellow]{Markup.Escape(TerminalText.Visible(refused.Message))}[/]";
        }
        // The format is read from the bytes here so a wrong file is said at once; the turn checks again when it is sent.
        if (ImageFiles.Inspect(image.Data) is not { } info)
            return $"[yellow]{Markup.Escape(TerminalText.Visible(argument))} is not a PNG, JPEG, GIF or WebP image (read from its bytes).[/]";
        staged.Add(image);
        return $"[grey]Staged {Markup.Escape(Describe(image, info))} for your next message ({Plural(staged.Count, "image")}).[/]";
    }

    /// <summary>The line shown as a message with images is sent.</summary>
    public static string Sending(IReadOnlyList<ImageInput> images) =>
        $"[grey]↳ with {Markup.Escape(List(images))}[/]";

    private static string List(IReadOnlyList<ImageInput> images) =>
        string.Join(", ", images.Select(image => ImageFiles.Inspect(image.Data) is { } info ? Describe(image, info) : ImageNames.Clean(image.Name) ?? "an image"));

    private static string Describe(ImageInput image, ImageInfo info) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{TerminalText.Visible(ImageNames.Clean(image.Name) ?? "image")} ({info.Width}×{info.Height} {ImageFiles.Kind(info.MediaType)}, {ImageFiles.Size(image.Data.Length)})");

    private static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? "" : "s")}");
}

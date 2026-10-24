using System.Xml.Linq;

namespace Corvid.App.Core.Tests;

/// <summary>
/// The parts of docs/plan/ui.md §4.15 that the markup alone decides, checked over every XAML file of the app so a new page
/// cannot drop them: a name on every icon-only button, a top-level heading on every page, and a live region on every
/// notice or error row a command fills. No screen reader runs in CI (nor an AT-SPI bridge in Uno's X11 head), so these
/// stand in for one where they can.
/// </summary>
public sealed class AccessibilityMarkupTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Controls = "using:Corvid.App.Controls";

    public static TheoryData<string> XamlFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(AppSource(), "*.xaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            data.Add(Path.GetRelativePath(AppSource(), file));
        return data;
    }

    public static TheoryData<string> PageFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppSource(), "Pages"), "*Page.xaml").Order(StringComparer.Ordinal))
            data.Add(Path.GetRelativePath(AppSource(), file));
        return data;
    }

    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void EveryIconOnlyButtonHasAName(string file)
    {
        var unnamed = Load(file).Descendants()
            .Where(IsIconOnlyButton)
            .Where(element => Attribute(element, "AutomationProperties.Name") is null)
            .Select(Describe)
            .ToList();

        Assert.True(unnamed.Count == 0, $"{file}: icon-only buttons without AutomationProperties.Name: {string.Join(", ", unnamed)}");
    }

    [Theory]
    [MemberData(nameof(PageFiles))]
    public void EveryPageHasATopLevelHeading(string file)
    {
        var headings = Load(file).Descendants().Count(element => Attribute(element, "AutomationProperties.HeadingLevel") == "Level1");

        Assert.True(headings >= 1, $"{file} has no element with AutomationProperties.HeadingLevel=\"Level1\".");
    }

    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void NoticeAndErrorRowsAreLiveRegions(string file)
    {
        // A ToneRow shown by a notice or an error flag: a command's result, which must be heard as well as seen.
        var silent = Load(file).Descendants(Controls + "ToneRow")
            .Where(row => Attribute(row, "Visibility") is { } visibility
                && (visibility.Contains("Notice", StringComparison.Ordinal) || visibility.Contains("Error", StringComparison.Ordinal)))
            .Where(row => Attribute(row, "AutomationProperties.LiveSetting") is null && !HasLiveAncestor(row))
            .Select(Describe)
            .ToList();

        Assert.True(silent.Count == 0, $"{file}: notice or error rows without AutomationProperties.LiveSetting: {string.Join(", ", silent)}");
    }

    private static bool IsIconOnlyButton(XElement element)
    {
        if (element.Name == Controls + "IconButton")
            return true;
        var isButton = element.Name == Presentation + "Button" || element.Name == Controls + "CorvidButton";
        if (!isButton || Attribute(element, "Content") is not null)
            return false;
        // The content is the element's children other than property elements (Button.Flyout and the like).
        var content = element.Elements().Where(child => !child.Name.LocalName.Contains('.', StringComparison.Ordinal)).ToList();
        return content.Count == 1 && content[0].Name == Controls + "Icon";
    }

    private static bool HasLiveAncestor(XElement element) =>
        element.Ancestors().Any(ancestor => Attribute(ancestor, "AutomationProperties.LiveSetting") is not null);

    private static string? Attribute(XElement element, string name) => element.Attribute(name)?.Value;

    private static string Describe(XElement element) =>
        $"{element.Name.LocalName} at line {((System.Xml.IXmlLineInfo)element).LineNumber}";

    private static XDocument Load(string file) => XDocument.Load(Path.Combine(AppSource(), file), LoadOptions.SetLineInfo);

    private static string AppSource()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Corvid.slnx")))
                return Path.Combine(directory.FullName, "src", "Corvid.App");
        }

        throw new InvalidOperationException("The repository root (Corvid.slnx) is not above the test's directory.");
    }
}

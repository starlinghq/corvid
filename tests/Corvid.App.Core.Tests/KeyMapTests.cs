using Corvid.App.Core.Keys;

namespace Corvid.App.Core.Tests;

/// <summary>The keyboard map: gestures as keybindings.json writes them, the defaults of docs/plan/ui.md §6, remapping and its checks.</summary>
public sealed class KeyMapTests
{
    private const string Override = "\u202E";

    [Theory]
    [InlineData("ctrl+k", "Ctrl+K")]
    [InlineData("Shift+Ctrl+f", "Ctrl+Shift+F")]
    [InlineData(" alt + p ", "Alt+P")]
    [InlineData("Ctrl+;", "Ctrl+;")]
    [InlineData("ctrl+semicolon", "Ctrl+;")]
    [InlineData("Ctrl+,", "Ctrl+,")]
    [InlineData("f1", "F1")]
    [InlineData("?", "?")]
    [InlineData("Ctrl+Tab", "Ctrl+Tab")]
    public void AGestureReadsInAnySpellingAndWritesOneWay(string text, string written)
    {
        Assert.True(KeyGesture.TryParse(text, out var gesture, out var problem), problem);
        Assert.Equal(written, gesture.ToString());
        Assert.Equal(KeyGesture.Parse(written), gesture);
    }

    [Theory]
    [InlineData("", "Type a key")]
    [InlineData("K", "would fire while you type")]
    [InlineData("Shift+K", "would fire while you type")]
    [InlineData("Hyper+K", "is not Ctrl, Shift or Alt")]
    [InlineData("Ctrl+Banana", "is not a key")]
    [InlineData("Ctrl+F13", "is not a key")]
    [InlineData("Ctrl+", "ends in '+'")]
    public void AGestureThatIsNotOneSaysWhy(string text, string reason)
    {
        Assert.False(KeyGesture.TryParse(text, out _, out var problem));
        Assert.Contains(reason, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AProblemNeverCarriesTheHiddenCharactersItQuotes()
    {
        Assert.False(KeyGesture.TryParse($"Ctrl+A{Override}B", out _, out var problem));
        Assert.DoesNotContain(Override, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultsBindEveryKeyOfTheMapWithNoConflict()
    {
        var keys = new KeyMap();
        string Key(string id) => keys[id].KeyText;
        Assert.Equal("Ctrl+K", Key(KeyCommands.Palette));
        Assert.Equal("Ctrl+P", Key(KeyCommands.PaletteAlias));
        Assert.Equal("Ctrl+N", Key(KeyCommands.NewSession));
        Assert.Equal("Ctrl+Shift+N", Key(KeyCommands.NewWindow));
        Assert.Equal("Ctrl+Tab", Key(KeyCommands.SessionSwitcher));
        Assert.Equal("Ctrl+;", Key(KeyCommands.Aside));
        Assert.Equal("Ctrl+F", Key(KeyCommands.Find));
        Assert.Equal("Ctrl+Shift+F", Key(KeyCommands.Search));
        Assert.Equal("Ctrl+R", Key(KeyCommands.History));
        Assert.Equal("Ctrl+B", Key(KeyCommands.ToggleDock));
        Assert.Equal("Ctrl+Shift+B", Key(KeyCommands.ToggleRail));
        Assert.Equal("Ctrl+Shift+C", Key(KeyCommands.Changes));
        Assert.Equal("Ctrl+,", Key(KeyCommands.Settings));
        Assert.Equal("Alt+P", Key(KeyCommands.SwitchGenerator));
        Assert.Equal("F1", Key(KeyCommands.Help));
        Assert.Equal("?", Key(KeyCommands.HelpAlias));
        Assert.All(keys.Bindings, binding => Assert.Empty(keys.ConflictsOf(binding)));
        Assert.False(keys.IsChanged);
        // What is not built yet says what it comes with.
        Assert.NotNull(keys[KeyCommands.NewSession].ComesWith);
        Assert.NotNull(keys[KeyCommands.Aside].ComesWith);
    }

    [Fact]
    public void APageKeyOnTheSameGestureShadowsTheWindowsKeyThere()
    {
        var keys = new KeyMap();
        var shadows = keys.ShadowsOf(keys[KeyCommands.Find]);
        Assert.Equal(["audit", "memory", "skills"], shadows.Select(shadow => shadow.Context).Order());
        Assert.Equal("models", Assert.Single(keys.ShadowsOf(keys[KeyCommands.SwitchGenerator])).Context);
        Assert.Empty(keys.ShadowsOf(keys[KeyCommands.Palette]));
    }

    [Fact]
    public void TheWindowLeavesAGestureToThePageThatShowsWithItsOwnKey()
    {
        var keys = new KeyMap();
        Assert.True(keys.IsPagesKey(keys[KeyCommands.Find], Area.Audit));
        Assert.True(keys.IsPagesKey(keys[KeyCommands.Find], Area.Memory));
        Assert.False(keys.IsPagesKey(keys[KeyCommands.Find], Area.Sessions));
        Assert.True(keys.IsPagesKey(keys[KeyCommands.Find], Area.Skills));
        Assert.False(keys.IsPagesKey(keys[KeyCommands.Find], Area.Jobs));
        Assert.True(keys.IsPagesKey(keys[KeyCommands.SwitchGenerator], Area.Models));
        Assert.False(keys.IsPagesKey(keys[KeyCommands.SwitchGenerator], Area.Sessions));
        Assert.False(keys.IsPagesKey(keys[KeyCommands.Palette], Area.Settings));

        // A remapping onto a page's key is left to that page where it shows.
        Assert.True(keys.Rebind(KeyCommands.Palette, "Ctrl+S").Accepted);
        Assert.True(keys.IsPagesKey(keys[KeyCommands.Palette], Area.Sessions));
        Assert.False(keys.IsPagesKey(keys[KeyCommands.Palette], Area.Board));
    }

    [Fact]
    public void ARemappingThatConflictsIsRefusedAndChangesNothing()
    {
        var keys = new KeyMap();
        var changes = 0;
        keys.Changed += (_, _) => changes++;

        var result = keys.Rebind(KeyCommands.History, "ctrl+k");

        Assert.False(result.Accepted);
        Assert.Contains("Ctrl+K already opens the command palette", result.Problem, StringComparison.Ordinal);
        Assert.Equal(KeyCommands.Palette, Assert.Single(result.Conflicts).Id);
        Assert.Equal("Ctrl+R", keys[KeyCommands.History].KeyText);
        Assert.Equal(0, changes);
        Assert.False(keys.Rebind(KeyCommands.History, "R").Accepted);
        Assert.False(keys.Rebind("composer.stash", "Ctrl+J").Accepted);
    }

    [Fact]
    public void ARemappingMovesTheKeyAndIsWrittenAsKeybindingsJson()
    {
        var keys = new KeyMap();
        var changes = 0;
        keys.Changed += (_, _) => changes++;

        var result = keys.Rebind(KeyCommands.History, "Ctrl+Shift+H");

        Assert.True(result.Accepted, result.Problem);
        Assert.Empty(result.Shadows);
        Assert.Equal(1, changes);
        Assert.True(keys.IsChanged);
        Assert.Equal(KeyCommands.History, keys.Find(KeyGesture.Parse("Ctrl+Shift+H"))?.Id);
        Assert.Null(keys.Find(KeyGesture.Parse("Ctrl+R")));
        var json = keys.ToJson();
        Assert.Contains("\"Ctrl+Shift+H\": \"history\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Ctrl+R\": null", json, StringComparison.Ordinal);

        keys.Reset(KeyCommands.History);
        Assert.Equal("Ctrl+R", keys[KeyCommands.History].KeyText);
        Assert.False(keys.IsChanged);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void ARemappingOntoAPagesKeyIsTakenWithTheShadowNamed()
    {
        var keys = new KeyMap();
        var result = keys.Rebind(KeyCommands.Palette, "Ctrl+S");
        Assert.True(result.Accepted);
        Assert.Equal("composer.stash", Assert.Single(result.Shadows).Id);
    }

    [Fact]
    public void PuttingBackADefaultSomeoneElseTookPutsThatOneBackToo()
    {
        var keys = new KeyMap();
        Assert.True(keys.Rebind(KeyCommands.History, "Ctrl+Shift+H").Accepted);
        Assert.True(keys.Rebind(KeyCommands.Search, "Ctrl+R").Accepted);

        keys.Reset(KeyCommands.History);

        Assert.Equal("Ctrl+R", keys[KeyCommands.History].KeyText);
        Assert.Equal("Ctrl+Shift+F", keys[KeyCommands.Search].KeyText);
        Assert.All(keys.Bindings, binding => Assert.Empty(keys.ConflictsOf(binding)));
        keys.ResetAll();
        Assert.False(keys.IsChanged);
    }

    [Fact]
    public void KeyTextBecomesCapsForTheHintLine()
    {
        Assert.Equal("[Ctrl] [Shift] [F]", Render(KeyParts.For("Ctrl+Shift+F")));
        Assert.Equal("[Ctrl] [,]", Render(KeyParts.For("Ctrl+,")));
        Assert.Equal("[Esc] [Esc]", Render(KeyParts.For("Esc Esc")));
        Assert.Equal("[Ctrl] [1] to [Ctrl] [7]", Render(KeyParts.For("Ctrl+1 to Ctrl+7")));
        Assert.Equal("[Up], [Down]", Render(KeyParts.For("Up, Down")));
        Assert.Equal("[Shift] [Enter]", Render(KeyParts.For("Shift+Enter")));
        Assert.Empty(KeyParts.For(""));

        static string Render(IEnumerable<Sessions.HintPart> parts) =>
            string.Concat(parts.Select(part => part.IsKey ? $"[{part.Text}]" : part.Text));
    }
}

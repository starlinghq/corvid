using Corvid.App.Core;
using Corvid.App.Pages;

namespace Corvid.App.Shell;

/// <summary>The page each area shows. Every area has one (<c>Pages/&lt;Area&gt;Page.xaml</c>), named after it.</summary>
internal static class AreaPages
{
    public static Type For(Area area) => area switch
    {
        Area.Sessions => typeof(SessionsPage),
        Area.Board => typeof(BoardPage),
        Area.Review => typeof(ReviewPage),
        Area.Rewind => typeof(RewindPage),
        Area.Memory => typeof(MemoryPage),
        Area.Skills => typeof(SkillsPage),
        Area.Jobs => typeof(JobsPage),
        Area.Models => typeof(ModelsPage),
        Area.Safety => typeof(SafetyPage),
        Area.Mcp => typeof(McpPage),
        Area.Gateway => typeof(GatewayPage),
        Area.Secrets => typeof(SecretsPage),
        Area.Audit => typeof(AuditPage),
        Area.Settings => typeof(SettingsPage),
        Area.FirstRun => typeof(FirstRunPage),
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, "Every area needs a page."),
    };
}

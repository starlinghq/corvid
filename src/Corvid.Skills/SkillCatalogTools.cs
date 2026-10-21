using System.Text.Json.Nodes;
using Corvid.Tools;

namespace Corvid.Skills;

/// <summary>
/// The skill index as a tool (L0, plan §11), for another agent through C-018's MCP server: the turn loop offers skills
/// through the <c>skill</c> question instead, so Corvid's own generator never sees this tool. Read-only.
/// </summary>
public sealed partial class SkillCatalogTools(SkillStore store)
{
    private readonly SkillStore _store = store ?? throw new ArgumentNullException(nameof(store));

    [Tool("skill_list", Description = "List the saved skills: each one's name and what it is for. Read one with skill_read.")]
    public JsonNode List()
    {
        var skills = new JsonArray();
        foreach (var skill in _store.Skills)
        {
            skills.Add((JsonNode)new JsonObject
            {
                ["name"] = skill.Name,
                ["description"] = skill.Document.Description,
                // A hub skill is outside content (plan §10.3); the reader should treat it as such.
                ["trusted"] = skill.Trusted,
            });
        }
        return new JsonObject { ["skills"] = skills };
    }
}

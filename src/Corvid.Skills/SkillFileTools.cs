using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using Corvid.Approvals;
using Corvid.Sandbox;
using Corvid.Tools;
using Corvid.Tools.Builtin;

namespace Corvid.Skills;

/// <summary>
/// The skill tools (plan §11, §10.6): <c>skill_read</c> loads a skill's body or one of its files (L1, L2), and
/// <c>skill_run_script</c> runs a script from a skill's <c>scripts/</c> folder. Scripts only ever run in the sandbox (the
/// shell's: workspace writable, no network, secrets hidden), and only after the approval pipeline allowed this call: the
/// tool is a side effect, so the turn loop asks first, and without a calibrated decider it always asks. Content from a hub
/// skill is outside content, so the call's result taints the session (§10.3).
/// </summary>
public sealed partial class SkillFileTools(SkillStore store, SandboxedShell? shell = null)
{
    public const string RunScript = "skill_run_script";

    private readonly SkillStore _store = store ?? throw new ArgumentNullException(nameof(store));

    [Tool(Kernel.Turns.SkillTools.Read, Description = "Read a saved skill: its instructions, or one of its files when a path is given.")]
    public JsonNode Read(
        [Description("The skill's name.")] string name,
        [Description("A file inside the skill, for example references/api.md. Omit it for the skill's instructions.")] string? file = null,
        ToolCallContext? context = null)
    {
        var skill = Find(name);
        string text;
        try
        {
            text = string.IsNullOrWhiteSpace(file) ? skill.Document.Body : _store.ReadResource(skill, file);
        }
        catch (ArgumentException exception)
        {
            throw new ToolArgumentException(exception.Message, nameof(file));
        }
        if (string.IsNullOrWhiteSpace(file))
            _store.Load(skill.Name);
        if (!skill.Trusted)
            context?.MarkResultTainted();
        var result = new JsonObject
        {
            ["skill"] = skill.Name,
            ["content"] = text,
        };
        if (string.IsNullOrWhiteSpace(file))
            result["files"] = new JsonArray([.. skill.Resources.Select(resource => (JsonNode)resource)]);
        if (!skill.Trusted)
            result["tainted"] = true;
        return result;
    }

    [Tool(RunScript, Description = "Run a script from a saved skill's scripts folder (.sh with bash, .py with python3), sandboxed in the workspace without network.")]
    [SideEffect(SideEffects.Process | SideEffects.Filesystem)]
    [Destructive]
    public async Task<JsonNode> RunScriptAsync(
        [Description("The skill's name.")] string name,
        [Description("The script, relative to the skill folder, for example scripts/report.sh.")] string script,
        ToolCallContext context,
        [Description("Arguments passed to the script.")] string[]? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (shell is null)
            throw new InvalidOperationException("Skill scripts need the sandboxed shell, which is not configured.");
        // The turn loop authorizes a side-effecting call before it runs; without that approval the script never runs.
        if (context.Approval is not ApprovalResult { Allowed: true })
            throw new InvalidOperationException("A skill script runs only after the approval pipeline has allowed this call.");
        var skill = Find(name);
        if (!script.Replace('\\', '/').StartsWith("scripts/", StringComparison.Ordinal))
            throw new ToolArgumentException("Only files in the skill's scripts/ folder run.", nameof(script));
        string path;
        try
        {
            path = SkillStore.ResolveInside(skill.Folder, script);
        }
        catch (ArgumentException exception)
        {
            throw new ToolArgumentException(exception.Message, nameof(script));
        }
        var interpreter = Path.GetExtension(path) switch
        {
            ".sh" or ".bash" => "/bin/bash",
            ".py" => "python3",
            _ => throw new ToolArgumentException("Only .sh and .py scripts run.", nameof(script)),
        };
        var command = new StringBuilder(interpreter).Append(' ').Append(Quote(path));
        foreach (var argument in arguments ?? [])
            command.Append(' ').Append(Quote(argument));

        SandboxResult result;
        try
        {
            result = await shell.RunAsync(command.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxRefusedException exception)
        {
            throw new InvalidOperationException("The sandbox refused to run the script: " + exception.Message, exception);
        }
        if (!skill.Trusted)
            context.MarkResultTainted();
        var output = new JsonObject
        {
            ["exit_code"] = result.ExitCode,
            ["stdout"] = Trim(result.StandardOutput),
            ["stderr"] = Trim(result.StandardError),
        };
        if (result.TimedOut)
            output["timed_out"] = true;
        if (!skill.Trusted)
            output["tainted"] = true;
        return output;
    }

    private Skill Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !_store.TryGet(name.Trim(), out var skill))
            throw new ToolArgumentException($"There is no skill named '{name}'. Known: {string.Join(", ", _store.Skills.Select(item => item.Name).Take(20))}.", nameof(name));
        return skill;
    }

    /// <summary>A single-quoted bash word: the script and its arguments reach bash as literal text, never expanded.</summary>
    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Trim(string text) => text.Length <= 16_000 ? text : text[..4_000] + "\n… [cut] …\n" + text[^12_000..];
}

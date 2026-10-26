using System.Text.Json;
using Corvid.Kernel.Turns;
using Corvid.Tools;

namespace Corvid.Kernel.Tests;

/// <summary>A5: <c>delegate_task</c>'s arguments as the kernel reads them (plan §14).</summary>
public sealed class DelegateTaskToolTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void One_goal_or_several_tasks_are_read_with_their_fields()
    {
        Assert.True(DelegateTaskTool.TryRead(Json("""
            {"goal":" count ","tools":["a","b"],"toolset":"read_only","mix":{"generator":"m"},"max_steps":3,"output_schema":{"type":"object"}}
            """), out var one, out _));
        var task = Assert.Single(one);
        Assert.Equal(("count", "read_only", 3), (task.Goal, task.Toolset, task.MaxSteps));
        Assert.Equal(["a", "b"], task.Tools);
        Assert.Equal("m", task.Mix!.Slots["generator"].Model);
        Assert.NotNull(task.OutputSchema);

        Assert.True(DelegateTaskTool.TryRead(Json("""{"tasks":[{"goal":"one"},{"goal":"two","max_seconds":30}]}"""), out var two, out _));
        Assert.Equal(["one", "two"], two.Select(item => item.Goal));
        Assert.Equal(30, two[1].MaxSeconds);
    }

    [Theory]
    [InlineData("""{}""", "'goal' is required")]
    [InlineData("""{"goal":"x","tasks":[{"goal":"y"}]}""", "not both")]
    [InlineData("""{"tasks":[]}""", "non-empty")]
    [InlineData("""{"tasks":[{"goal":"y"},{"tools":["a"]}]}""", "tasks[1]: 'goal' is required")]
    [InlineData("""{"goal":"x","max_steps":0}""", "positive whole number")]
    [InlineData("""{"goal":"x","tools":"a"}""", "array of tool names")]
    [InlineData("""{"goal":"x","mix":{"nope":"m"}}""", "'mix'")]
    [InlineData("""{"goal":"x","output_schema":"object"}""", "JSON Schema object")]
    public void A_malformed_call_says_what_is_wrong(string arguments, string expected)
    {
        Assert.False(DelegateTaskTool.TryRead(Json(arguments), out _, out var error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void One_call_starts_at_most_ten_children()
    {
        var tasks = string.Join(",", Enumerable.Range(0, DelegateTaskTool.MaxTasks + 1).Select(i => $"{{\"goal\":\"g{i}\"}}"));
        Assert.False(DelegateTaskTool.TryRead(Json($"{{\"tasks\":[{tasks}]}}"), out _, out var error));
        Assert.Contains("at most 10", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Spawning_is_a_side_effect_so_it_is_approved_per_the_sessions_mode()
    {
        var tool = new DelegateTaskTool((_, _, _) => Task.FromResult<object?>(null));
        Assert.False(tool.Descriptor.Risk.ReadOnly);
        Assert.Equal(SideEffects.Process, tool.Descriptor.Risk.SideEffects);
        Assert.Equal(DelegateTaskTool.ToolName, tool.Name);
    }
}

using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;

namespace Corvid.Kernel.Tests;

/// <summary>A5 (switch): a call names the generator that asked for it, so a replay places a switch mid-turn exactly.</summary>
public sealed class GeneratorPerCallTests
{
    [Fact]
    public void A_calls_generator_is_persisted_when_known_and_left_out_when_not()
    {
        var arguments = Samples.Json("""{"path":"a"}""");
        var named = EventJson.Serialize(new ToolCallIssued("c1", "read_file", arguments, 2, true, Generator: "qwen3.5-0.8b@cuda0"));
        Assert.Equal("""{"type":"tool_call_issued","call_id":"c1","tool_name":"read_file","arguments":{"path":"a"},"round":2,"read_only":true,"generator":"qwen3.5-0.8b@cuda0"}""", named);
        Assert.Equal("qwen3.5-0.8b@cuda0", Assert.IsType<ToolCallIssued>(EventJson.Deserialize(EventJson.Version, named)).Generator);

        // The user's own ! call, and every event written before A5, have none: their persisted form is unchanged.
        var plain = EventJson.Serialize(new ToolCallIssued("c2", "shell", arguments, 1));
        Assert.DoesNotContain("generator", plain, StringComparison.Ordinal);
        Assert.Null(Assert.IsType<ToolCallIssued>(EventJson.Deserialize(EventJson.Version, plain)).Generator);
    }
}

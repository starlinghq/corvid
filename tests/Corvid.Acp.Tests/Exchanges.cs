using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Corvid.Acp.Tests;

/// <summary>
/// Recorded message exchanges (<c>Exchanges/*.jsonl</c>): every message the client sent, then every message the agent sent,
/// each in order, with what changes from run to run replaced (session ids, tool call ids, the workspace path, times, the version). The two
/// directions are kept apart because only the order within each is fixed: a notification queued after a response can reach
/// the client after the client's next request. <c>CORVID_RECORD_ACP=1</c> rewrites the recordings instead of comparing;
/// review that diff, since recording accepts whatever the agent says today.
/// </summary>
internal static partial class Exchanges
{
    public static void Verify(string name, AcpClient client, [CallerFilePath] string source = "")
    {
        var lines = Normalize(client);
        var recorded = Path.Combine(Path.GetDirectoryName(source)!, "Exchanges", name + ".jsonl");
        if (Environment.GetEnvironmentVariable("CORVID_RECORD_ACP") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(recorded)!);
            File.WriteAllText(recorded, string.Join("\n", lines) + "\n");
            return;
        }
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Exchanges", name + ".jsonl"))
            .Where(line => line.Length > 0).ToList();
        for (var i = 0; i < Math.Max(expected.Count, lines.Count); i++)
            Assert.True(i < expected.Count && i < lines.Count && expected[i] == lines[i],
                $"{name}.jsonl line {i + 1}:\n expected {(i < expected.Count ? expected[i] : "(nothing)")}\n actual   {(i < lines.Count ? lines[i] : "(nothing)")}");
    }

    private static List<string> Normalize(AcpClient client)
    {
        List<(string From, JsonObject Message)> exchange;
        lock (client.Exchange)
            exchange = [.. client.Exchange];
        var sessions = new Dictionary<string, string>(StringComparer.Ordinal);
        // The fake generator numbers calls across the whole test process, so an id depends on which tests ran first.
        var calls = new Dictionary<string, string>(StringComparer.Ordinal);
        var workspace = client.Workspace;
        string Text(string value)
        {
            foreach (var match in SessionId().Matches(value).Select(match => match.Value))
                if (!sessions.ContainsKey(match))
                    sessions[match] = $"<session-{sessions.Count + 1}>";
            foreach (var (id, placeholder) in sessions)
                value = value.Replace(id, placeholder, StringComparison.Ordinal);
            value = value.Replace(workspace, "<workspace>", StringComparison.Ordinal);
            return Deadline().Replace(value, "at <time>");
        }
        JsonNode? Walk(JsonNode? node, string? key)
        {
            switch (node)
            {
                case JsonObject obj:
                    var copy = new JsonObject();
                    foreach (var (name, value) in obj)
                        copy[name] = name is "updatedAt" || name == "version" && key == "agentInfo" ? "<varies>" : Walk(value, name);
                    return copy;
                case JsonArray array:
                    return new JsonArray([.. array.Select(item => Walk(item, key))]);
                case JsonValue value when key == "toolCallId" && value.TryGetValue<string>(out var call):
                    if (!calls.TryGetValue(call, out var placeholder))
                        calls[call] = placeholder = $"<call-{calls.Count + 1}>";
                    return JsonValue.Create(placeholder);
                case JsonValue value when value.TryGetValue<string>(out var text):
                    return JsonValue.Create(Text(text));
                default:
                    return node?.DeepClone();
            }
        }
        return [.. exchange.Where(item => item.From == "client").Concat(exchange.Where(item => item.From == "agent"))
            .Select(item => new JsonObject { ["from"] = item.From, ["message"] = Walk(item.Message, null) }.ToJsonString())];
    }

    [GeneratedRegex(@"acp-\d{8}-\d{6}-[0-9a-f]{6}")]
    private static partial Regex SessionId();

    [GeneratedRegex(@"at \d{2}:\d{2}:\d{2}")]
    private static partial Regex Deadline();
}

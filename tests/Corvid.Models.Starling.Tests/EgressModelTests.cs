using System.Text.Json;
using Corvid.Abstractions.Extraction;
using Corvid.Kernel.Turns;
using Corvid.Models.Privacy;
using Microsoft.Extensions.AI;

namespace Corvid.Models.Starling.Tests;

/// <summary>
/// A5 (A4 leftover): the egress gate with the real GLiNER2 on realistic turns, as <c>Privacy:LocalServer: Redact</c> sends
/// them. Opt in with <c>CORVID_STARLING_MODELS</c> (see <see cref="ModelTests"/>). In the A4 end-to-end pass JSON keys,
/// "status", "details" and the system prompt's words were replaced; the person's name and email must still go.
/// </summary>
public sealed class EgressModelTests
{
    private static readonly ModelProfile Server = new("lan-model", 32_000, 32_000, ToolCallSupport.Native, StructuredOutputSupport.JsonSchema,
        PromptCachingStyle.Automatic, ModelCost.Free, PrivacyClass.LocalServer, "http://127.0.0.1:8080");

    [Fact]
    public async Task Gliner2_redacts_the_persons_values_and_leaves_the_turns_structure()
    {
        var cache = Environment.GetEnvironmentVariable(ModelTests.CacheVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(cache), $"Set {ModelTests.CacheVariable} to a Starling model cache to run model-backed tests.");
        var store = new StarlingModelStore(cache);
        var status = store.Check(StarlingModel.Gliner2, Starlings.Acquisition.CacheVerification.Size);
        Assert.SkipWhen(status.State == StarlingModelState.Missing, status.Describe());
        var bound = StarlingBindings.Gliner2Extractor(store.OpenGliner2());
        try
        {
            var egress = new PrivacyEgress(bound.GetService<IExtractor>(), new PrivacyEgressOptions
            {
                PolicyFor = _ => EgressPolicy.Default,
                TrustedTexts = [TurnOptions.DefaultSystemPrompt],
            });
            List<ChatMessage> turn =
            [
                new(ChatRole.System, TurnOptions.DefaultSystemPrompt + "\n\nCore memory:\n- The user's name is Hannah Okafor."),
                new(ChatRole.User, "What is the status of the nightly build? If it failed, send the details to hannah.okafor@example.org."),
                new(ChatRole.Assistant, [new FunctionCallContent("c1", "shell", new Dictionary<string, object?> { ["command"] = "ci status --json nightly" })]),
                new(ChatRole.Tool, [new FunctionResultContent("c1", JsonDocument.Parse("""
                    {"status":"failed","details":{"job":"nightly","step":"test","exit_code":1,"message":"3 tests failed in Corvid.Host.Tests"},"owner":"Hannah Okafor"}
                    """).RootElement.Clone())]),
            ];

            var prepared = await egress.PrepareAsync(Server, turn, new ModelRequestContext("s1", Tainted: false), TestContext.Current.CancellationToken);

            var sent = string.Join("\n", prepared.Messages.SelectMany(message => message.Contents).Select(content => content switch
            {
                TextContent text => text.Text,
                FunctionCallContent call => JsonSerializer.Serialize(call.Arguments),
                FunctionResultContent { Result: JsonElement json } => json.GetRawText(),
                _ => "",
            }));
            TestContext.Current.TestOutputHelper?.WriteLine(sent);
            Assert.DoesNotContain("Hannah Okafor", sent, StringComparison.Ordinal);
            Assert.DoesNotContain("hannah.okafor@example.org", sent, StringComparison.Ordinal);
            Assert.StartsWith(TurnOptions.DefaultSystemPrompt, sent, StringComparison.Ordinal);
            foreach (var kept in (string[])["\"status\":", "\"details\":", "\"exit_code\":", "\"owner\":", "status of the nightly build", "ci status --json nightly",
                "3 tests failed in Corvid.Host.Tests", "The user's name is <PERSON_1>."])
                Assert.Contains(kept, sent, StringComparison.Ordinal);

            // The person's words alone, with no JSON to say what is structure.
            List<ChatMessage> plain =
            [
                new(ChatRole.System, TurnOptions.DefaultSystemPrompt),
                new(ChatRole.User, "Can you check the status of my order and send me the details? I'm Hannah Okafor, hannah.okafor@example.org."),
            ];
            var second = await egress.PrepareAsync(Server, plain, new ModelRequestContext("s2", Tainted: false), TestContext.Current.CancellationToken);
            var asked = second.Messages[1].Text;
            TestContext.Current.TestOutputHelper?.WriteLine(asked);
            Assert.Equal(TurnOptions.DefaultSystemPrompt, second.Messages[0].Text);
            Assert.StartsWith("Can you check the status of my order and send me the details?", asked, StringComparison.Ordinal);
            Assert.DoesNotContain("Hannah Okafor", asked, StringComparison.Ordinal);
            Assert.DoesNotContain("hannah.okafor@example.org", asked, StringComparison.Ordinal);
        }
        finally
        {
            (bound.Service as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task Gliner2_at_the_default_threshold_leaves_the_nouns_of_a_turn_about_logins()
    {
        // A5 review: the default threshold is 0.4 again and a credential span of letters is a value unless it is a credential
        // noun. On this turn GLiNER2 labels "John Smith" a person and "handle" a credential (a noun, so kept); it labels
        // neither "sunshinerainbow" nor "SmithJ", and the credential detector's prose gate (C-032) wants a digit or a symbol,
        // so both are sent as written: see docs/plan/ui-handoff.md §1.6 (left).
        var cache = Environment.GetEnvironmentVariable(ModelTests.CacheVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(cache), $"Set {ModelTests.CacheVariable} to a Starling model cache to run model-backed tests.");
        var store = new StarlingModelStore(cache);
        var status = store.Check(StarlingModel.Gliner2, Starlings.Acquisition.CacheVerification.Size);
        Assert.SkipWhen(status.State == StarlingModelState.Missing, status.Describe());
        var bound = StarlingBindings.Gliner2Extractor(store.OpenGliner2());
        try
        {
            var egress = new PrivacyEgress(bound.GetService<IExtractor>(), new PrivacyEgressOptions
            {
                PolicyFor = _ => EgressPolicy.Default,
                TrustedTexts = [TurnOptions.DefaultSystemPrompt],
            });
            List<ChatMessage> turn =
            [
                new(ChatRole.System, TurnOptions.DefaultSystemPrompt),
                new(ChatRole.User, "my wifi password is sunshinerainbow. Also ask John Smith about the build; his handle is SmithJ."),
            ];

            var prepared = await egress.PrepareAsync(Server, turn, new ModelRequestContext("s3", Tainted: false), TestContext.Current.CancellationToken);

            var sent = prepared.Messages[1].Text;
            TestContext.Current.TestOutputHelper?.WriteLine(sent);
            Assert.Equal(TurnOptions.DefaultSystemPrompt, prepared.Messages[0].Text);
            Assert.DoesNotContain("John Smith", sent, StringComparison.Ordinal);
            Assert.StartsWith("my wifi password is ", sent, StringComparison.Ordinal);
            Assert.Contains("about the build; his handle is ", sent, StringComparison.Ordinal);
        }
        finally
        {
            (bound.Service as IDisposable)?.Dispose();
        }
    }
}

using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Tokens;

namespace Corvid.Fabric.Tests;

public sealed class TurnStateTests
{
    [Fact]
    public void Renders_the_plan_fields_in_order()
    {
        var state = new TurnStateBuilder().Build(Fixtures.Input());

        Assert.Equal(["user_message", "surface", "recent", "goal", "entities", "step", "last_tool_result", "generator"],
            state.Json.EnumerateObject().Select(property => property.Name));
        Assert.Equal("can you move tomorrow's dentist reminder to friday and tell sam", state.Json.GetProperty("user_message").GetString());
        Assert.Equal(JsonValueKind.Null, state.Json.GetProperty("goal").ValueKind);
        Assert.Equal(["tomorrow", "friday"], state.Json.GetProperty("entities").GetProperty("date").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(state.Truncated);
        Assert.False(state.ExceedsHardStop);
        Assert.Equal(CharsPerTokenCounter.Instance.CountTokens(state.ToJsonString()), state.Tokens);
        Assert.InRange(state.Tokens, 1, 600);
    }

    [Fact]
    public void A_steered_turn_shows_the_steering_after_the_message_and_cuts_it_before_the_message()
    {
        var plain = new TurnStateBuilder().Build(Fixtures.Input());
        var steered = new TurnStateBuilder().Build(Fixtures.Input() with { Steering = "also check the retry count" });

        // K-3: only a steered state has the field, right after the message; every other state keeps its bytes.
        Assert.False(plain.Json.TryGetProperty("steering", out _));
        Assert.Equal(["user_message", "steering", "surface", "recent", "goal", "entities", "step", "last_tool_result", "generator"],
            steered.Json.EnumerateObject().Select(property => property.Name));
        Assert.Equal("also check the retry count", steered.Json.GetProperty("steering").GetString());
        Assert.Equal(plain.ToJsonString(), new TurnStateBuilder().Build(Fixtures.Input() with { Steering = "" }).ToJsonString());

        // Over the total, the steering is cut after the rest and before the user's message, which stays whole here.
        var tight = new TurnStateBuilder(budget: new TurnStateBudget(Total: 100, Steering: 80)).Build(Fixtures.Input("hi") with
        {
            Steering = new string('s', 300),
        });
        Assert.Contains("steering", tight.Truncated);
        Assert.DoesNotContain("user_message", tight.Truncated);
        Assert.Equal("hi", tight.Json.GetProperty("user_message").GetString());
        Assert.InRange(tight.Tokens, 1, 100);
    }

    [Fact]
    public void Default_counter_is_chars_over_four_rounded_up()
    {
        Assert.Equal(0, CharsPerTokenCounter.Instance.CountTokens(""));
        Assert.Equal(1, CharsPerTokenCounter.Instance.CountTokens("abcd"));
        Assert.Equal(2, CharsPerTokenCounter.Instance.CountTokens("abcde"));
    }

    [Fact]
    public void Long_fields_are_cut_to_their_budgets()
    {
        var input = Fixtures.Input(new string('m', 4_000)) with
        {
            Goal = new string('g', 1_000),
            LastToolResult = new string('r', 1_000),
            Recent = [.. Enumerable.Range(0, 100).Select(i => $"event {i:D3} " + new string('e', 40))],
        };

        var state = new TurnStateBuilder().Build(input);

        var message = state.Json.GetProperty("user_message").GetString()!;
        Assert.EndsWith("…", message, StringComparison.Ordinal);
        Assert.InRange(CharsPerTokenCounter.Instance.CountTokens(message), 1, TurnStateBudget.Default.UserMessage);
        Assert.InRange(CharsPerTokenCounter.Instance.CountTokens(state.Json.GetProperty("goal").GetString()!), 1, TurnStateBudget.Default.Goal);
        // The newest events survive; the oldest are dropped.
        var recent = state.Json.GetProperty("recent").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.StartsWith("event 099", recent[^1], StringComparison.Ordinal);
        Assert.DoesNotContain(recent, item => item.StartsWith("event 000", StringComparison.Ordinal));
        Assert.Contains("user_message", state.Truncated);
        Assert.Contains("recent", state.Truncated);
        Assert.InRange(state.Tokens, 1, TurnStateBudget.Default.Total);
        Assert.False(state.ExceedsHardStop);
    }

    [Fact]
    public void Truncation_is_deterministic()
    {
        var input = Fixtures.Input(string.Concat(Enumerable.Repeat("dentist friday sam ", 300))) with { LastToolResult = new string('x', 900) };

        var first = new TurnStateBuilder().Build(input).ToJsonString();
        var second = new TurnStateBuilder().Build(input with { }).ToJsonString();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Truncation_never_splits_a_surrogate_pair()
    {
        var builder = new TurnStateBuilder();
        var text = string.Concat(Enumerable.Repeat("a😀", 50));

        for (var budget = 1; budget < 30; budget++)
        {
            var cut = builder.Truncate(text, budget);
            Assert.InRange(CharsPerTokenCounter.Instance.CountTokens(cut), 0, budget);
            var withoutEllipsis = cut.TrimEnd('…');
            Assert.False(withoutEllipsis.Length > 0 && char.IsHighSurrogate(withoutEllipsis[^1]), $"split pair at budget {budget}");
        }
        Assert.Same(text, builder.Truncate(text, 1_000));
        Assert.Equal("", builder.Truncate(text, 0));
    }

    [Fact]
    public void Whole_state_is_trimmed_to_the_total_in_a_fixed_order()
    {
        // Field budgets add to more than the total, so the whole-state pass must cut further, starting with `recent`.
        var budget = new TurnStateBudget(Total: 150, UserMessage: 100, Recent: 100, LastToolResult: 100);
        var input = Fixtures.Input(new string('m', 1_000)) with
        {
            Recent = [.. Enumerable.Range(0, 40).Select(i => $"event {i}")],
            LastToolResult = new string('r', 1_000),
        };

        var state = new TurnStateBuilder(budget: budget).Build(input);

        Assert.InRange(state.Tokens, 1, 150);
        Assert.Equal(0, state.Json.GetProperty("recent").GetArrayLength());
        Assert.Equal(["user_message", "recent", "last_tool_result"], state.Truncated);
        Assert.False(state.ExceedsHardStop);
    }

    [Fact]
    public void Fixed_fields_over_the_hard_stop_are_flagged()
    {
        // Surface and generator are never cut by the whole-state pass; a misconfigured budget can push them over.
        var budget = new TurnStateBudget(Surface: 4_000);
        var input = Fixtures.Input() with { Surface = new string('s', 8_000) };

        var state = new TurnStateBuilder(budget: budget).Build(input);

        Assert.True(state.ExceedsHardStop);
        Assert.True(state.Tokens > TurnStateBudget.Default.HardStop);
        Assert.Equal("", state.Json.GetProperty("user_message").GetString());
    }

    [Fact]
    public void Budgets_are_validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnStateBuilder(budget: new TurnStateBudget(Total: 2_000)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnStateBuilder(budget: new TurnStateBudget(Goal: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnStateBuilder().Build(Fixtures.Input() with { Step = -1 }));
    }

    [Fact]
    public void A_custom_counter_drives_the_budgets()
    {
        var words = new WordCounter();
        var state = new TurnStateBuilder(words, new TurnStateBudget(UserMessage: 3)).Build(Fixtures.Input("one two three four five"));

        Assert.Equal("one two three…", state.Json.GetProperty("user_message").GetString());
        Assert.Equal(words.CountTokens(state.ToJsonString()), state.Tokens);
    }

    [Fact]
    public void Entities_keep_ordinal_label_order_within_budget()
    {
        var input = Fixtures.Input() with
        {
            Entities = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal,
            [
                KeyValuePair.Create("task", ImmutableArray.Create("dentist reminder")),
                KeyValuePair.Create("date", ImmutableArray.CreateRange(Enumerable.Range(0, 200).Select(i => $"day {i}"))),
            ]),
        };

        var state = new TurnStateBuilder().Build(input);

        var entities = state.Json.GetProperty("entities");
        Assert.Equal("date", entities.EnumerateObject().First().Name);
        Assert.InRange(entities.GetProperty("date").GetArrayLength(), 1, 199);
        Assert.Contains("entities", state.Truncated);
    }

    private sealed class WordCounter : ITokenCounter
    {
        public int CountTokens(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    }
}

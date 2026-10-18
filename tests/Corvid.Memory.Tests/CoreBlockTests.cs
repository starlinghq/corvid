namespace Corvid.Memory.Tests;

public sealed class CoreBlockTests
{
    [Fact]
    public void The_three_blocks_exist_empty_with_their_default_budgets()
    {
        using var store = new TempStore();
        var snapshot = store.Blocks.Snapshot();
        Assert.Equal(["agent", "persona", "user"], snapshot.Blocks.Select(block => block.Name));
        Assert.All(snapshot.Blocks, block => Assert.True(block.IsEmpty));
        Assert.Equal(350, snapshot["user"].BudgetTokens);
        Assert.Equal(550, snapshot["agent"].BudgetTokens);
        Assert.Equal(500, snapshot["persona"].BudgetTokens);
        Assert.Throws<ArgumentException>(() => store.Blocks.Get("memory"));
    }

    [Fact]
    public void A_write_within_budget_applies_and_bumps_the_revision()
    {
        using var store = new TempStore();
        store.Time.Advance(TimeSpan.FromHours(1));
        var result = store.Blocks.Set("user", "Mark. Prefers metric units.");
        Assert.Equal(BlockWriteStatus.Applied, result.Status);
        Assert.Equal(1, result.Block.Revision);
        Assert.Equal(store.Time.GetUtcNow(), result.Block.UpdatedAt);
        Assert.Equal("Mark. Prefers metric units.", new CoreBlockStore(store.Database).Get("user").Content);
    }

    [Fact]
    public void A_write_over_budget_or_against_a_stale_revision_changes_nothing()
    {
        using var store = new TempStore();
        store.Blocks.SetBudget("agent", 5);
        Assert.Equal(1, store.Blocks.Get("agent").Revision);

        var over = store.Blocks.Set("agent", new string('x', 21));
        Assert.Equal(BlockWriteStatus.OverBudget, over.Status);
        Assert.Equal(6, over.ContentTokens);
        Assert.True(store.Blocks.Get("agent").IsEmpty);

        Assert.Equal(BlockWriteStatus.Applied, store.Blocks.Set("agent", new string('x', 20)).Status);
        var stale = store.Blocks.Set("agent", "late", expectedRevision: 1);
        Assert.Equal(BlockWriteStatus.Conflict, stale.Status);
        Assert.Equal(new string('x', 20), store.Blocks.Get("agent").Content);
    }

    [Fact]
    public void A_snapshot_is_frozen_and_renders_local_only_blocks_only_for_local_models()
    {
        using var store = new TempStore();
        store.Blocks.Set("user", "Lives in Leeds.");
        store.Blocks.Set("agent", "Use British spelling.");
        store.Blocks.SetLocalOnly("user", true);
        var snapshot = store.Blocks.Snapshot();

        store.Blocks.Set("agent", "Changed later.");
        Assert.Equal("Use British spelling.", snapshot["agent"].Content);

        Assert.Equal("[agent]\nUse British spelling.\n\n[user]\nLives in Leeds.\n", snapshot.Render(includeLocalOnly: true));
        Assert.Equal("[agent]\nUse British spelling.\n", snapshot.Render(includeLocalOnly: false));
        Assert.True(store.Blocks.Get("user").LocalOnly);
    }
}

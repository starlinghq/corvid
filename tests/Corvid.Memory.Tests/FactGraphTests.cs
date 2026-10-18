using Microsoft.Data.Sqlite;
using static Corvid.Memory.Tests.Fixtures;

namespace Corvid.Memory.Tests;

public sealed class FactGraphTests
{
    [Fact]
    public void A_relation_links_two_entities_matched_by_label_and_normalized_name()
    {
        using var store = new TempStore();
        var fact = store.Facts.Add(new FactInput("Sam works at Acme",
            Relation: new RelationInput(Person("Sam"), "works_at", new EntityRef("Organization", "Acme"))));

        Assert.StartsWith("fact:", fact.FactId);
        Assert.Equal("works_at", fact.Predicate);
        Assert.Equal(["acme", "sam"], fact.Entities.Select(entity => entity.Name.ToLowerInvariant()).Order());
        Assert.Equal(fact.SubjectId, store.Facts.FindEntities([Person("  SAM ")]).Single().EntityId);
        Assert.Empty(store.Facts.FindEntities([Place("Sam")]));

        var second = store.Facts.Add(new FactInput("Sam likes tea", [Person("sam")]));
        Assert.Equal(fact.SubjectId, second.Entities.Single().EntityId);
    }

    [Fact]
    public void Invalidation_sets_valid_to_and_keeps_the_fact_answerable_as_of_earlier_times()
    {
        using var store = new TempStore();
        store.Time.Set(January);
        var paris = store.Facts.Add(new FactInput("Mark lives in Paris", [Person("Mark"), Place("Paris")]));
        store.Time.Set(May);
        var closed = store.Facts.Invalidate(paris.FactId, validTo: new DateTimeOffset(2026, 4, 20, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 4, 20, 0, 0, 0, TimeSpan.Zero), closed.ValidTo);
        Assert.Equal(May, closed.InvalidatedAt);
        Assert.False(closed.IsCurrent);
        Assert.NotNull(store.Facts.Get(paris.FactId));

        // What was true in March, as known now: Paris.
        Assert.Equal(paris.FactId, Assert.Single(store.Facts.AsOf(March)).FactId);
        // What is true in June, as known now: nothing.
        Assert.Empty(store.Facts.AsOf(June));
        // What did Corvid believe about June back in early April, before it learned of the move: still Paris.
        Assert.Single(store.Facts.AsOf(June, knownAt: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)));
        // Before the fact was recorded nothing was known.
        Assert.Empty(store.Facts.AsOf(March, knownAt: January.AddDays(-1)));
        Assert.Single(store.Facts.AsOf(March, entity: Place("paris")));
        Assert.Empty(store.Facts.AsOf(March, entity: Place("Lyon")));

        Assert.Throws<InvalidOperationException>(() => store.Facts.Invalidate(paris.FactId));
        Assert.Empty(store.Facts.Search("Paris"));
    }

    [Fact]
    public void Supersede_ends_the_old_fact_when_the_new_one_starts()
    {
        using var store = new TempStore();
        var old = store.Facts.Add(new FactInput("Mark lives in Paris", [Person("Mark")]));
        store.Time.Set(May);
        var (closed, added) = store.Facts.Supersede(old.FactId, new FactInput("Mark lives in Leeds", [Person("Mark")]));

        Assert.Equal(May, closed.ValidTo);
        Assert.Equal(May, added.ValidFrom);
        Assert.Equal(old.FactId, added.Supersedes);
        Assert.Equal(["Mark lives in Leeds"], store.Facts.AsOf(June).Select(fact => fact.Text));
        Assert.Equal(["Mark lives in Paris"], store.Facts.AsOf(March).Select(fact => fact.Text));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Facts.Invalidate(added.FactId, validTo: March));
    }

    [Fact]
    public void Facts_are_never_deleted_or_rewritten()
    {
        using var store = new TempStore();
        var fact = store.Facts.Add(new FactInput("Sam likes tea", [Person("Sam")]));
        using var connection = store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM facts;";
        Assert.Contains("never deleted", Assert.Throws<SqliteException>(() => command.ExecuteNonQuery()).Message);
        command.CommandText = "UPDATE facts SET text='Sam likes coffee';";
        Assert.Contains("closing its validity", Assert.Throws<SqliteException>(() => command.ExecuteNonQuery()).Message);
        command.CommandText = "DELETE FROM fact_entities;";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());

        store.Facts.Invalidate(fact.FactId);
        command.CommandText = "UPDATE facts SET valid_to=NULL, invalidated_at=NULL;";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void Expansion_reaches_one_hop_from_the_seed_entities_and_no_further()
    {
        using var store = new TempStore();
        var works = store.Facts.Add(new FactInput("Sam works at Acme",
            Relation: new RelationInput(Person("Sam"), "works_at", new EntityRef("organization", "Acme"))));
        store.Time.Advance(TimeSpan.FromMinutes(1));
        var acme = store.Facts.Add(new FactInput("Acme is in Leeds",
            Relation: new RelationInput(new EntityRef("organization", "Acme"), "located_in", Place("Leeds"))));
        store.Time.Advance(TimeSpan.FromMinutes(1));
        store.Facts.Add(new FactInput("Leeds has a market", [Place("Leeds")]));
        var stale = store.Facts.Add(new FactInput("Sam owns a bike", [Person("Sam")]));
        store.Facts.Invalidate(stale.FactId);

        var sam = store.Facts.FindEntities([Person("Sam")]).Select(entity => entity.EntityId);
        var hits = store.Facts.Expand(sam);
        Assert.Equal([(works.FactId, 0), (acme.FactId, 1)], hits.Select(hit => (hit.Fact.FactId, hit.Hops)));
        Assert.Single(store.Facts.Expand(sam, limit: 1));
        Assert.Empty(store.Facts.Expand([]));
    }
}

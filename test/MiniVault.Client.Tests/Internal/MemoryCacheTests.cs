using MiniVault.Client.Internal;

namespace MiniVault.Client.Tests.Internal;

public class MemoryCacheTests
{
    private static CachedSecret Make(string name, string value = "v", int version = 1) =>
        new CachedSecret(name, System.Text.Encoding.UTF8.GetBytes(value), "text/plain", version,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void TryGet_ReturnsFalse_WhenAbsent()
    {
        var cache = new MemoryCache();
        cache.TryGet("missing", out var entry).ShouldBeFalse();
        entry.ShouldBeNull();
    }

    [Fact]
    public void Set_ThenTryGet_ReturnsTheSameEntry()
    {
        var cache = new MemoryCache();
        var entry = Make("db/password");

        cache.Set(entry);

        cache.TryGet("db/password", out var got).ShouldBeTrue();
        got.ShouldBeSameAs(entry);
    }

    [Fact]
    public void Set_Overwrites_ExistingEntryWithSameName()
    {
        var cache = new MemoryCache();
        cache.Set(Make("db/password", version: 1));
        cache.Set(Make("db/password", version: 2));

        cache.TryGet("db/password", out var got).ShouldBeTrue();
        got!.Version.ShouldBe(2);
    }

    [Fact]
    public void Remove_DeletesEntry()
    {
        var cache = new MemoryCache();
        cache.Set(Make("db/password"));

        cache.Remove("db/password");

        cache.TryGet("db/password", out _).ShouldBeFalse();
    }

    [Fact]
    public void Remove_OfMissingEntry_DoesNotThrow()
    {
        var cache = new MemoryCache();
        Should.NotThrow(() => cache.Remove("missing"));
    }

    [Fact]
    public void Snapshot_ReturnsAllEntries()
    {
        var cache = new MemoryCache();
        cache.Set(Make("a"));
        cache.Set(Make("b"));

        var snapshot = cache.Snapshot();

        snapshot.Count.ShouldBe(2);
        snapshot.Select(e => e.Name).ShouldContain("a");
        snapshot.Select(e => e.Name).ShouldContain("b");
    }

    [Fact]
    public void Snapshot_OnEmptyCache_IsEmpty()
    {
        var cache = new MemoryCache();
        cache.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public void Keys_AreOrdinal_CaseSensitive()
    {
        var cache = new MemoryCache();
        cache.Set(Make("Secret"));
        cache.Set(Make("secret"));

        cache.Snapshot().Count.ShouldBe(2);
        cache.TryGet("Secret", out var upper).ShouldBeTrue();
        cache.TryGet("secret", out var lower).ShouldBeTrue();
        upper.ShouldNotBeSameAs(lower);
    }

    [Fact]
    public void TryGet_WithDifferentCasing_DoesNotMatch()
    {
        var cache = new MemoryCache();
        cache.Set(Make("Secret"));

        cache.TryGet("SECRET", out _).ShouldBeFalse();
    }
}

using System.Text;
using MiniVault.Client.Internal;

namespace MiniVault.Client.Tests.Internal;

public class DiskCacheTests : IDisposable
{
    private readonly string _dir;

    public DiskCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "minivault-disk-cache-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static CachedSecret Make(string name, string value, string? contentType, int version, DateTimeOffset updatedAt, DateTimeOffset fetchedAt) =>
        new CachedSecret(name, Encoding.UTF8.GetBytes(value), contentType, version, updatedAt, fetchedAt);

    [Fact]
    public void Load_OnMissingFile_ReturnsEmpty_WithoutLogging()
    {
        var logged = new List<string>();
        var cache = new DiskCache(_dir, "client", "secret", logged.Add);

        var entries = cache.Load();

        entries.ShouldBeEmpty();
        logged.ShouldBeEmpty();
    }

    [Fact]
    public void FilePath_IsDirectory_CombinedWith_ClientIdAndCacheExtension()
    {
        var cache = new DiskCache(_dir, "my-client", "secret", null);
        cache.FilePath.ShouldBe(Path.Combine(_dir, "my-client.cache"));
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips_PreservingOffsetsAndContentType()
    {
        var cache = new DiskCache(_dir, "client", "secret", null);
        var updatedAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(3));
        var fetchedAt = new DateTimeOffset(2026, 3, 4, 6, 0, 0, TimeSpan.FromHours(-5));
        var entries = new[]
        {
            Make("db/password", "s3cr3t", "text/plain", 7, updatedAt, fetchedAt),
            Make("no-content-type", "value2", null, 1, updatedAt, fetchedAt),
        };

        cache.Save(entries);
        var loaded = cache.Load();

        loaded.Count.ShouldBe(2);

        var first = loaded.Single(e => e.Name == "db/password");
        first.Value.ShouldBe(Encoding.UTF8.GetBytes("s3cr3t"));
        first.ContentType.ShouldBe("text/plain");
        first.Version.ShouldBe(7);
        first.UpdatedAt.ShouldBe(updatedAt);
        first.UpdatedAt.Offset.ShouldBe(updatedAt.Offset);
        first.FetchedAt.ShouldBe(fetchedAt);
        first.FetchedAt.Offset.ShouldBe(fetchedAt.Offset);

        var second = loaded.Single(e => e.Name == "no-content-type");
        second.ContentType.ShouldBeNull();
    }

    [Fact]
    public void TwoDiskCaches_WithSameIdAndSecret_ReadEachOthersFile()
    {
        var a = new DiskCache(_dir, "client", "shared-secret", null);
        var b = new DiskCache(_dir, "client", "shared-secret", null);
        var entry = Make("x", "y", null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        a.Save(new[] { entry });
        var loaded = b.Load();

        loaded.Count.ShouldBe(1);
        loaded[0].Name.ShouldBe("x");
    }

    [Fact]
    public void Load_WithDifferentClientSecret_ReturnsEmpty_AndLogs()
    {
        var writer = new DiskCache(_dir, "client", "secret-one", null);
        writer.Save(new[] { Make("x", "y", null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) });

        var logged = new List<string>();
        var reader = new DiskCache(_dir, "client", "secret-two", logged.Add);

        var loaded = reader.Load();

        loaded.ShouldBeEmpty();
        logged.ShouldHaveSingleItem();
        logged[0].ShouldContain("cache");
    }

    [Fact]
    public void Load_OnCorruptFile_ReturnsEmpty_AndLogs()
    {
        Directory.CreateDirectory(_dir);
        var cache = new DiskCache(_dir, "client", "secret", null);
        var random = new byte[256];
        new Random(42).NextBytes(random);
        File.WriteAllBytes(cache.FilePath, random);

        var logged = new List<string>();
        var reader = new DiskCache(_dir, "client", "secret", logged.Add);

        var loaded = reader.Load();

        loaded.ShouldBeEmpty();
        logged.ShouldHaveSingleItem();
        logged[0].ShouldContain("cache");
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        Directory.Exists(_dir).ShouldBeFalse();
        var cache = new DiskCache(_dir, "client", "secret", null);

        cache.Save(new[] { Make("x", "y", null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) });

        Directory.Exists(_dir).ShouldBeTrue();
        File.Exists(cache.FilePath).ShouldBeTrue();
    }

    [Fact]
    public void SavedFile_DoesNotContain_PlaintextValueOrName()
    {
        var cache = new DiskCache(_dir, "client", "secret", null);
        const string secretValue = "super-secret-plaintext-value";
        const string secretName = "very-unique-secret-name";
        cache.Save(new[] { Make(secretName, secretValue, "text/plain", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) });

        var bytes = File.ReadAllBytes(cache.FilePath);
        var asText = Encoding.UTF8.GetString(bytes);
        var asBase64OfValue = Convert.ToBase64String(Encoding.UTF8.GetBytes(secretValue));

        asText.ShouldNotContain(secretValue);
        asText.ShouldNotContain(secretName);
        asText.ShouldNotContain(asBase64OfValue);
    }

    [Fact]
    public async Task ConcurrentSaves_DoNotThrow_AndFileIsLoadableAfterwards()
    {
        var cache = new DiskCache(_dir, "client", "secret", null);

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            try
            {
                cache.Save(new[] { Make("x", "value-" + i, null, i, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) });
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        exceptions.ShouldBeEmpty();
        var loaded = cache.Load();
        loaded.Count.ShouldBe(1);
        loaded[0].Name.ShouldBe("x");
    }

    [Fact]
    public async Task TwoInstances_SameFile_ParallelSaves_DoNotThrow()
    {
        var a = new DiskCache(_dir, "client", "shared-secret", null);
        var b = new DiskCache(_dir, "client", "shared-secret", null);

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var tasks = Enumerable.Range(0, 10).SelectMany(i => new[]
        {
            Task.Run(() =>
            {
                try
                {
                    a.Save(new[] { Make("x", "a-value-" + i, null, i, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) });
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }),
            Task.Run(() =>
            {
                try
                {
                    b.Save(new[] { Make("y", "b-value-" + i, null, i, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) });
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }),
        }).ToArray();

        await Task.WhenAll(tasks);

        exceptions.ShouldBeEmpty();

        var reader = new DiskCache(_dir, "client", "shared-secret", null);
        var loaded = reader.Load();

        loaded.ShouldNotBeEmpty();
        loaded.ShouldAllBe(e => e.Name == "x" || e.Name == "y");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Karmasis.MiniVault.Server.Data;
using Karmasis.MiniVault.Server.Keys;
using Karmasis.MiniVault.Server.Secrets;
using Karmasis.MiniVault.Server.Tests.TestDoubles;
using Karmasis.MiniVault.Server.Vault;

namespace Karmasis.MiniVault.Server.Tests.Secrets;

public class SecretServiceTests : IAsyncLifetime
{
    private VaultFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await VaultFixture.CreateAsync();
    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Set_ThenGet_RoundTrips_Version1()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<SecretService>();
        var value = "hello"u8.ToArray();

        var version = await sut.SetAsync("a/b", value, "text/plain", "tester", CancellationToken.None);

        version.ShouldBe(1);
        var record = await sut.GetAsync("a/b", CancellationToken.None);
        record.Name.ShouldBe("a/b");
        record.Value.ShouldBe(value);
        record.ContentType.ShouldBe("text/plain");
        record.Version.ShouldBe(1);
    }

    [Fact]
    public async Task Set_Again_IncrementsVersion_AndKeepsContentType()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<SecretService>();
        await sut.SetAsync("a/b", "v1"u8.ToArray(), "text/plain", "tester", CancellationToken.None);

        var version = await sut.SetAsync("a/b", "v2"u8.ToArray(), "text/plain", "tester", CancellationToken.None);

        version.ShouldBe(2);
        var record = await sut.GetAsync("a/b", CancellationToken.None);
        record.Value.ShouldBe("v2"u8.ToArray());
        record.ContentType.ShouldBe("text/plain");
        record.Version.ShouldBe(2);
    }

    [Fact]
    public async Task Get_Unknown_Throws_SecretNotFound()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<SecretService>();

        var ex = await Should.ThrowAsync<SecretNotFoundException>(() => sut.GetAsync("missing", CancellationToken.None));
        ex.Name.ShouldBe("missing");
    }

    [Fact]
    public async Task Delete_ThenGet_Throws()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<SecretService>();
        await sut.SetAsync("a/b", "v"u8.ToArray(), null, "tester", CancellationToken.None);

        await sut.DeleteAsync("a/b", CancellationToken.None);

        await Should.ThrowAsync<SecretNotFoundException>(() => sut.GetAsync("a/b", CancellationToken.None));
    }

    [Fact]
    public async Task List_ByPrefix_ReturnsOrderedNamesWithoutValues()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<SecretService>();
        await sut.SetAsync("app/b", "1"u8.ToArray(), null, "tester", CancellationToken.None);
        await sut.SetAsync("app/a", "2"u8.ToArray(), null, "tester", CancellationToken.None);
        await sut.SetAsync("app-x/c", "3"u8.ToArray(), null, "tester", CancellationToken.None);

        var items = await sut.ListAsync("app/", CancellationToken.None);

        items.Select(i => i.Name).ShouldBe(["app/a", "app/b"]);
        items.ShouldAllBe(i => i.Version == 1);
    }

    [Fact]
    public async Task Set_InvalidName_ThrowsValidationException()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<SecretService>();

        await Should.ThrowAsync<SecretValidationException>(() => sut.SetAsync("bad name", "v"u8.ToArray(), null, "tester", CancellationToken.None));
    }

    [Fact]
    public async Task Set_TooLarge_ThrowsValidationException()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<SecretService>();
        var value = new byte[SecretService.MaxValueBytes + 1];

        await Should.ThrowAsync<SecretValidationException>(() => sut.SetAsync("a/b", value, null, "tester", CancellationToken.None));
    }

    [Fact]
    public async Task Set_ConcurrentUpdate_ThrowsConflict()
    {
        using (var scope0 = _fixture.CreateScope())
        {
            var sut0 = scope0.ServiceProvider.GetRequiredService<SecretService>();
            await sut0.SetAsync("a/b", "v1"u8.ToArray(), null, "tester", CancellationToken.None);
        }

        using var scopeA = _fixture.CreateScope();
        using var scopeB = _fixture.CreateScope();

        // Scope B tracks the row before scope A writes. SecretService.SetAsync re-queries by name, but EF Core's
        // identity resolution hands back the already-tracked (now stale) instance instead of refreshing it from
        // the database, so scope B's save uses a stale RowVersion and collides with scope A's write.
        var ctxB = scopeB.ServiceProvider.GetRequiredService<MiniVaultDbContext>();
        await ctxB.Secrets.SingleAsync(s => s.Name == "a/b", CancellationToken.None);

        var sutA = scopeA.ServiceProvider.GetRequiredService<SecretService>();
        await sutA.SetAsync("a/b", "v2"u8.ToArray(), null, "tester", CancellationToken.None);

        var sutB = scopeB.ServiceProvider.GetRequiredService<SecretService>();
        await Should.ThrowAsync<SecretConflictException>(() => sutB.SetAsync("a/b", "v3"u8.ToArray(), null, "tester", CancellationToken.None));
    }

    [Fact]
    public async Task Get_CaseVariant_NotFound()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<SecretService>();
        await sut.SetAsync("a/b", "v1"u8.ToArray(), null, "tester", CancellationToken.None);

        // The stored name is "a/b"; "a/B" is a different secret, and it does not exist.
        await Should.ThrowAsync<SecretNotFoundException>(() => sut.GetAsync("a/B", CancellationToken.None));
        (await sut.GetVersionAsync("a/B", CancellationToken.None)).ShouldBeNull();
        (await sut.ListAsync("a/B", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetVersion_ReturnsNullWhenMissing()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<SecretService>();

        var version = await sut.GetVersionAsync("missing", CancellationToken.None);

        version.ShouldBeNull();
    }

    [Fact]
    public async Task Get_AfterRotateDek_StillDecrypts_AndNewSetUsesNewVersion()
    {
        using var scope = _fixture.CreateScope();
        var sut = scope.ServiceProvider.GetRequiredService<SecretService>();
        await sut.SetAsync("a/b", "v1"u8.ToArray(), null, "tester", CancellationToken.None);

        await using (var checkCtx = _fixture.Db.CreateContext())
            (await checkCtx.Secrets.AsNoTracking().SingleAsync(s => s.Name == "a/b")).DekVersion.ShouldBe(1);

        await using (var rotateCtx = _fixture.Db.CreateContext())
            await new VaultRecovery(rotateCtx, _fixture.Provider, TimeProvider.System).RotateDekAsync(CancellationToken.None);
        await _fixture.ServiceProvider.GetRequiredService<DataKeyRing>().ReloadAsync(CancellationToken.None);

        var record = await sut.GetAsync("a/b", CancellationToken.None);
        record.Value.ShouldBe("v1"u8.ToArray());

        await sut.SetAsync("a/b", "v2"u8.ToArray(), null, "tester", CancellationToken.None);

        await using var finalCtx = _fixture.Db.CreateContext();
        (await finalCtx.Secrets.AsNoTracking().SingleAsync(s => s.Name == "a/b")).DekVersion.ShouldBe(2);
    }
}

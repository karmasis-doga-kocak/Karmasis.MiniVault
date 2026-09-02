using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MiniVault.Server.Auth;
using MiniVault.Server.Data;
using MiniVault.Server.Data.Entities;
using MiniVault.Server.Keys;
using MiniVault.Server.Tests.TestDoubles;
using MiniVault.Server.Vault;
using Microsoft.EntityFrameworkCore;

namespace MiniVault.Server.Tests.Auth;

public class TokenServiceTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private readonly InMemoryMasterKeyProvider _provider = new();
    private ServiceProvider _sp = null!;
    private DataKeyRing _ring = null!;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync(migrate: false);
        await using (var ctx = _db.CreateContext())
            await new VaultInitializer(ctx, _provider, TimeProvider.System).InitializeAsync(new InitOptions(RecoveryMode.Single), CancellationToken.None);
        var services = new ServiceCollection();
        services.AddDbContext<MiniVaultDbContext>(o => o.UseSqlServer(_db.ConnectionString));
        services.AddSingleton<IMasterKeyProvider>(_provider);
        services.AddSingleton<DataKeyRing>();
        _sp = services.BuildServiceProvider();
        _ring = _sp.GetRequiredService<DataKeyRing>();
        await _ring.LoadAsync(CancellationToken.None);
    }
    public async Task DisposeAsync() { await _sp.DisposeAsync(); await _db.DisposeAsync(); }

    [Fact]
    public async Task Issue_ProducesValidatableHs256Token_WithClaims()
    {
        var sut = new TokenService(_ring, Options.Create(new TokenOptions { LifetimeMinutes = 15 }), _clock);

        var (token, expiresIn) = sut.Issue("collector-1", ["reader", "writer"]);

        expiresIn.ShouldBe(900);
        var result = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = TokenService.Issuer, ValidAudience = TokenService.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(_ring.JwtSigningKey),
            ValidateLifetime = false,
        });
        result.IsValid.ShouldBeTrue(result.Exception?.Message);
        result.Claims[TokenService.SubjectClaim].ShouldBe("collector-1");
        var jwt = new JsonWebToken(token);
        jwt.Claims.Where(c => c.Type == TokenService.RoleClaim).Select(c => c.Value).ShouldBe(["reader", "writer"], ignoreOrder: true);
        jwt.ValidTo.ShouldBe(_clock.GetUtcNow().AddMinutes(15).UtcDateTime);
    }

    [Fact]
    public void Issue_WithWrongKey_DoesNotValidate()
    {
        var sut = new TokenService(_ring, Options.Create(new TokenOptions()), _clock);
        var (token, _) = sut.Issue("c", []);

        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        var result = handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = TokenService.Issuer, ValidAudience = TokenService.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(new byte[32]), ValidateLifetime = false,
        }).GetAwaiter().GetResult();
        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void AddMiniVaultAuth_ConfiguresJwtBearerOptions_FromRing()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ring);
        var config = new ConfigurationBuilder().Build();
        services.AddMiniVaultAuth(config);
        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

        options.MapInboundClaims.ShouldBeFalse();
        options.TokenValidationParameters.ValidIssuer.ShouldBe(TokenService.Issuer);
        options.TokenValidationParameters.RoleClaimType.ShouldBe(TokenService.RoleClaim);
        var keys = options.TokenValidationParameters.IssuerSigningKeyResolver!(null!, null!, null!, null!);
        var key = keys.Single().ShouldBeOfType<SymmetricSecurityKey>();
        key.Key.ShouldBe(_ring.JwtSigningKey);
    }
}

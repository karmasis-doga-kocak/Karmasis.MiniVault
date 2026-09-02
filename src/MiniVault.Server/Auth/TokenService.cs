using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MiniVault.Server.Keys;

namespace MiniVault.Server.Auth;

public sealed class TokenService(DataKeyRing ring, IOptions<TokenOptions> options, TimeProvider clock)
{
    public const string Issuer = "minivault";
    public const string Audience = "minivault";
    public const string RoleClaim = "role";
    public const string SubjectClaim = "sub";

    public (string Token, int ExpiresInSeconds) Issue(string clientId, IEnumerable<string> roles)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var lifetime = TimeSpan.FromMinutes(options.Value.LifetimeMinutes);
        var claims = new List<Claim> { new(SubjectClaim, clientId) };
        claims.AddRange(roles.Select(r => new Claim(RoleClaim, r)));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(lifetime),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(ring.JwtSigningKey), SecurityAlgorithms.HmacSha256),
        };
        var token = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
        return (token, (int)lifetime.TotalSeconds);
    }
}

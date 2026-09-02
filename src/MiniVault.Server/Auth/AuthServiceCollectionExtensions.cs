using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using MiniVault.Server.Keys;

namespace MiniVault.Server.Auth;

public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddMiniVaultAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TokenOptions>(configuration.GetSection(TokenOptions.SectionName));
        services.AddSingleton<TokenService>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<IServiceProvider>((options, sp) =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = TokenService.Issuer,
                ValidAudience = TokenService.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeyResolver = (_, _, _, _) => [new SymmetricSecurityKey(sp.GetRequiredService<DataKeyRing>().JwtSigningKey)],
                NameClaimType = TokenService.SubjectClaim,
                RoleClaimType = TokenService.RoleClaim,
                ClockSkew = TimeSpan.FromSeconds(30),
            };
        });
        services.AddAuthorization();
        return services;
    }
}

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using MiniVault.Contracts;
using MiniVault.Server.Api;
using MiniVault.Server.Audit;
using MiniVault.Server.Keys;

namespace MiniVault.Server.Auth;

public static class AuthServiceCollectionExtensions
{
    /// <summary>Rate-limiting policy applied to the credential-taking token endpoint.</summary>
    public const string TokenRateLimitPolicy = "token";

    public static IServiceCollection AddMiniVaultAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TokenOptions>(configuration.GetSection(TokenOptions.SectionName));
        services.AddSingleton<TokenService>();

        var permitPerMinute = configuration.GetValue<int?>($"{TokenOptions.SectionName}:{nameof(TokenOptions.LoginRateLimitPerMinute)}")
            ?? new TokenOptions().LoginRateLimitPerMinute;
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddFixedWindowLimiter(TokenRateLimitPolicy, w =>
            {
                w.Window = TimeSpan.FromMinutes(1);
                w.PermitLimit = Math.Max(1, permitPerMinute);
                w.QueueLimit = 0;
            });
        });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<IServiceProvider>((options, sp) =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = TokenService.Issuer,
                ValidAudience = TokenService.Audience,
                ValidateIssuerSigningKey = true,
                // The signing key is a symmetric HKDF output; pinning the algorithm stops a token that names another
                // algorithm from ever being handed to the validator.
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                IssuerSigningKeyResolver = (_, _, _, _) => [new SymmetricSecurityKey(sp.GetRequiredService<DataKeyRing>().JwtSigningKey)],
                NameClaimType = TokenService.SubjectClaim,
                RoleClaimType = TokenService.RoleClaim,
                ClockSkew = TimeSpan.FromSeconds(30),
            };
            options.Events = new JwtBearerEvents
            {
                OnChallenge = async ctx =>
                {
                    ctx.HandleResponse();
                    // A rejected token is the only authentication failure the token endpoint's own audit row cannot
                    // record, so it is audited here; AuditWriter writes on its own context and truncates the detail.
                    var audit = ctx.HttpContext.RequestServices.GetRequiredService<AuditWriter>();
                    await audit.WriteAsync("(anonymous)", "token.rejected", null, false, ctx.HttpContext.RemoteIp(),
                        ctx.ErrorDescription ?? "missing or invalid bearer token", ctx.HttpContext.RequestAborted);
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsJsonAsync(new ErrorResponse { Error = ErrorResponse.Unauthorized });
                },
                OnForbidden = async ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsJsonAsync(new ErrorResponse { Error = ErrorResponse.Forbidden });
                },
            };
        });
        services.AddAuthorization();
        return services;
    }
}

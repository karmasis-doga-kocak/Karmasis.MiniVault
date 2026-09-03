using System.Security.Claims;
using Karmasis.MiniVault.Server.Auth;

namespace Karmasis.MiniVault.Server.Api;

public static class ApiExtensions
{
    /// <summary>
    /// Every route pattern the API maps. Kept next to <see cref="MapMiniVaultApi"/> so the documentation
    /// consistency test can check the <c>/v1/...</c> paths in the docs against something, and asserted against the
    /// live endpoint list by <c>RoutePatternsTests</c>, so an added or renamed endpoint cannot drift from it.
    /// </summary>
    internal static readonly string[] RoutePatterns =
    [
        "/v1/health",
        "/v1/auth/token",
        "/v1/secrets/",
        "/v1/secrets/{**name}",
    ];

    public static string ClientId(this ClaimsPrincipal user) => user.FindFirstValue(TokenService.SubjectClaim) ?? throw new UnauthorizedAccessException();
    public static IReadOnlyList<string> Roles(this ClaimsPrincipal user) => user.FindAll(TokenService.RoleClaim).Select(c => c.Value).ToList();
    public static string? RemoteIp(this HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

    public static WebApplication MapMiniVaultApi(this WebApplication app)
    {
        app.MapHealthEndpoints();
        app.MapAuthEndpoints();
        app.MapSecretEndpoints();
        return app;
    }
}

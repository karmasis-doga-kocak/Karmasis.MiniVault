using System.Security.Claims;
using MiniVault.Server.Auth;

namespace MiniVault.Server.Api;

public static class ApiExtensions
{
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

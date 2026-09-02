using Microsoft.AspNetCore.RateLimiting;
using MiniVault.Contracts;
using MiniVault.Server.Audit;
using MiniVault.Server.Auth;

namespace MiniVault.Server.Api;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/auth/token", async (TokenRequest request, ClientDirectory clients, TokenService tokens, AuditWriter audit, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
                return Results.BadRequest(new ErrorResponse { Error = ErrorResponse.InvalidRequest, Detail = "clientId and clientSecret are required." });

            var attempted = SanitizeClientId(request.ClientId);
            var identity = await clients.AuthenticateAsync(request.ClientId, request.ClientSecret, ct);
            if (identity is null)
            {
                await audit.WriteAsync(attempted, "token", null, false, http.RemoteIp(), "invalid credentials", ct);
                return Results.Json(new ErrorResponse { Error = ErrorResponse.Unauthorized }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var (token, expiresIn) = tokens.Issue(identity.ClientId, identity.Roles);
            await audit.WriteAsync(identity.ClientId, "token", null, true, http.RemoteIp(), null, ct);
            return Results.Ok(new TokenResponse { AccessToken = token, ExpiresIn = expiresIn });
        }).AllowAnonymous().RequireRateLimiting(AuthServiceCollectionExtensions.TokenRateLimitPolicy);
    }

    /// <summary>An unauthenticated caller chooses this string, and it is stored in the audit trail and read back by
    /// operators, so it is reduced to the character set a real client id uses before it is recorded.</summary>
    private static string SanitizeClientId(string value)
    {
        var kept = value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').Take(128).ToArray();
        return kept.Length == 0 ? "(invalid)" : new string(kept);
    }
}

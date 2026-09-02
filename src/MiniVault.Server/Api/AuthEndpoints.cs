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

            var attempted = request.ClientId.Length > 128 ? request.ClientId[..128] : request.ClientId;
            var identity = await clients.AuthenticateAsync(request.ClientId, request.ClientSecret, ct);
            if (identity is null)
            {
                await audit.WriteAsync(attempted, "token", null, false, http.RemoteIp(), "invalid credentials", ct);
                return Results.Json(new ErrorResponse { Error = ErrorResponse.Unauthorized }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var (token, expiresIn) = tokens.Issue(identity.ClientId, identity.Roles);
            await audit.WriteAsync(identity.ClientId, "token", null, true, http.RemoteIp(), null, ct);
            return Results.Ok(new TokenResponse { AccessToken = token, ExpiresIn = expiresIn });
        }).AllowAnonymous();
    }
}

using Karmasis.MiniVault.Contracts;
using Karmasis.MiniVault.Server.Keys;

namespace Karmasis.MiniVault.Server.Api;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/health", (DataKeyRing ring) => Results.Ok(new HealthResponse
        {
            Status = "ok", Initialized = ring.IsLoaded, ActiveDataKeyVersion = ring.IsLoaded ? ring.ActiveVersion : 0,
        })).AllowAnonymous();
    }
}

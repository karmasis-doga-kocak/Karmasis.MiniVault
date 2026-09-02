using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MiniVault.Contracts;
using MiniVault.Server.Keys;
using MiniVault.Server.Secrets;
using MiniVault.Server.Vault;

namespace MiniVault.Server.Api;

public static class ErrorHandling
{
    public static IApplicationBuilder UseMiniVaultErrorHandling(this IApplicationBuilder app) =>
        app.UseExceptionHandler(builder => builder.Run(async context =>
        {
            var ex = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("MiniVault.Api");
            var (status, body) = ex switch
            {
                SecretNotFoundException => (404, new ErrorResponse { Error = ErrorResponse.NotFound }),
                SecretConflictException => (409, new ErrorResponse { Error = ErrorResponse.Conflict, Detail = "The secret was modified concurrently; retry." }),
                ArgumentException or BadHttpRequestException or JsonException => (400, new ErrorResponse { Error = ErrorResponse.InvalidRequest, Detail = ex.Message }),
                VaultException or KeyNotFoundException or MasterKeyUnavailableException or SqlException or DbUpdateException => (503, new ErrorResponse { Error = ErrorResponse.VaultUnavailable, Detail = "The vault is temporarily unavailable." }),
                _ => (500, new ErrorResponse { Error = "internal_error" }),
            };
            if (status >= 500) logger.LogError(ex, "Request failed with {Status}", status);
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(body);
        }));
}

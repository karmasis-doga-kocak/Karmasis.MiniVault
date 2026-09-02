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
    /// <summary>Nginx's convention for "the client closed the request before a response was produced".</summary>
    private const int StatusClientClosedRequest = 499;

    public static IApplicationBuilder UseMiniVaultErrorHandling(this IApplicationBuilder app) =>
        app.UseExceptionHandler(builder => builder.Run(async context =>
        {
            var ex = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("MiniVault.Api");

            if (ex is OperationCanceledException)
            {
                // A caller that hung up gets no body (nothing could be delivered anyway); a cancellation that did not
                // come from the caller means the server gave up on the work, which is a 503 like any other outage.
                if (context.RequestAborted.IsCancellationRequested)
                {
                    context.Response.StatusCode = StatusClientClosedRequest;
                    return;
                }
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new ErrorResponse { Error = ErrorResponse.VaultUnavailable, Detail = "The vault is temporarily unavailable." });
                return;
            }

            var (status, body) = ex switch
            {
                SecretNotFoundException => (404, new ErrorResponse { Error = ErrorResponse.NotFound }),
                SecretConflictException => (409, new ErrorResponse { Error = ErrorResponse.Conflict, Detail = "The secret was modified concurrently; retry." }),
                UnauthorizedAccessException => (401, new ErrorResponse { Error = ErrorResponse.Unauthorized }),
                // Only SecretValidationException carries a message written for clients; every other argument failure
                // gets a fixed detail so internal parameter names and paths never reach the wire.
                SecretValidationException => (400, new ErrorResponse { Error = ErrorResponse.InvalidRequest, Detail = ex.Message }),
                BadHttpRequestException or JsonException => (400, new ErrorResponse { Error = ErrorResponse.InvalidRequest, Detail = "The request body could not be read as JSON." }),
                ArgumentException => (400, new ErrorResponse { Error = ErrorResponse.InvalidRequest, Detail = "Invalid request." }),
                VaultException or UnknownDataKeyException or MasterKeyUnavailableException or SqlException or DbUpdateException => (503, new ErrorResponse { Error = ErrorResponse.VaultUnavailable, Detail = "The vault is temporarily unavailable." }),
                _ => (500, new ErrorResponse { Error = "internal_error" }),
            };
            if (status >= 500) logger.LogError(ex, "Request failed with {Status}", status);
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(body);
        }));

    /// <summary>
    /// Gives the two status codes the pipeline produces without ever reaching an endpoint body — 405 from routing and
    /// 415 from request-body binding — the same JSON error shape as every other failure.
    /// </summary>
    public static IApplicationBuilder UseMiniVaultStatusCodePages(this IApplicationBuilder app) =>
        app.UseStatusCodePages(async ctx =>
        {
            if (ctx.HttpContext.Response.StatusCode is StatusCodes.Status405MethodNotAllowed or StatusCodes.Status415UnsupportedMediaType)
            {
                await ctx.HttpContext.Response.WriteAsJsonAsync(new ErrorResponse
                {
                    Error = ErrorResponse.InvalidRequest,
                    Detail = ctx.HttpContext.Response.StatusCode == StatusCodes.Status405MethodNotAllowed
                        ? "Method not allowed."
                        : "Unsupported media type; send application/json.",
                });
            }
        });
}

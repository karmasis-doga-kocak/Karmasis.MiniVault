using System.Security.Claims;
using MiniVault.Contracts;
using MiniVault.Server.Audit;
using MiniVault.Server.Auth;
using MiniVault.Server.Data.Entities;
using MiniVault.Server.Secrets;

namespace MiniVault.Server.Api;

public static class SecretEndpoints
{
    public static void MapSecretEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/secrets").RequireAuthorization();

        group.MapGet("/{**name}", async (string name, ClaimsPrincipal user, ClientDirectory clients, SecretService secrets, AuditWriter audit, HttpContext http, CancellationToken ct) =>
        {
            if (!SecretName.IsValid(name)) return Results.BadRequest(new ErrorResponse { Error = ErrorResponse.InvalidRequest, Detail = "Invalid secret name." });
            var clientId = user.ClientId();
            var rules = await clients.GetRulesAsync(user.Roles(), ct);
            if (!Authorizer.HasPermission(rules, name, Permission.Read))
            {
                await audit.WriteAsync(clientId, "secret.read", name, false, http.RemoteIp(), "forbidden", ct);
                return Results.Json(new ErrorResponse { Error = ErrorResponse.Forbidden }, statusCode: StatusCodes.Status403Forbidden);
            }
            var version = await secrets.GetVersionAsync(name, ct);
            if (version is null) { await audit.WriteAsync(clientId, "secret.read", name, false, http.RemoteIp(), "not found", ct); return Results.Json(new ErrorResponse { Error = ErrorResponse.NotFound }, statusCode: 404); }
            var etag = $"\"{version}\"";
            if (http.Request.Headers.IfNoneMatch.Any(v => v == etag))
            {
                await audit.WriteAsync(clientId, "secret.read", name, true, http.RemoteIp(), "not-modified", ct);
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
            var record = await secrets.GetAsync(name, ct);
            await audit.WriteAsync(clientId, "secret.read", name, true, http.RemoteIp(), null, ct);
            http.Response.Headers.ETag = $"\"{record.Version}\"";
            return Results.Ok(new SecretResponse { Name = record.Name, Value = Convert.ToBase64String(record.Value), ContentType = record.ContentType, Version = record.Version, UpdatedAt = record.UpdatedAt });
        });

        group.MapPut("/{**name}", async (string name, SetSecretRequest request, ClaimsPrincipal user, ClientDirectory clients, SecretService secrets, AuditWriter audit, HttpContext http, CancellationToken ct) =>
        {
            if (!SecretName.IsValid(name)) return Results.BadRequest(new ErrorResponse { Error = ErrorResponse.InvalidRequest, Detail = "Invalid secret name." });
            var clientId = user.ClientId();
            var rules = await clients.GetRulesAsync(user.Roles(), ct);
            if (!Authorizer.HasPermission(rules, name, Permission.Write))
            {
                await audit.WriteAsync(clientId, "secret.write", name, false, http.RemoteIp(), "forbidden", ct);
                return Results.Json(new ErrorResponse { Error = ErrorResponse.Forbidden }, statusCode: StatusCodes.Status403Forbidden);
            }

            byte[] value;
            try { value = Convert.FromBase64String(request.Value ?? ""); }
            catch (FormatException)
            {
                await audit.WriteAsync(clientId, "secret.write", name, false, http.RemoteIp(), "invalid base64", ct);
                return Results.BadRequest(new ErrorResponse { Error = ErrorResponse.InvalidRequest, Detail = "Value must be base64-encoded." });
            }

            var version = await secrets.SetAsync(name, value, request.ContentType, clientId, ct);
            await audit.WriteAsync(clientId, "secret.write", name, true, http.RemoteIp(), null, ct);
            return Results.Ok(new SetSecretResponse { Version = version });
        });

        group.MapDelete("/{**name}", async (string name, ClaimsPrincipal user, ClientDirectory clients, SecretService secrets, AuditWriter audit, HttpContext http, CancellationToken ct) =>
        {
            if (!SecretName.IsValid(name)) return Results.BadRequest(new ErrorResponse { Error = ErrorResponse.InvalidRequest, Detail = "Invalid secret name." });
            var clientId = user.ClientId();
            var rules = await clients.GetRulesAsync(user.Roles(), ct);
            if (!Authorizer.HasPermission(rules, name, Permission.Write))
            {
                await audit.WriteAsync(clientId, "secret.delete", name, false, http.RemoteIp(), "forbidden", ct);
                return Results.Json(new ErrorResponse { Error = ErrorResponse.Forbidden }, statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                await secrets.DeleteAsync(name, ct);
            }
            catch (SecretNotFoundException)
            {
                await audit.WriteAsync(clientId, "secret.delete", name, false, http.RemoteIp(), "not found", ct);
                return Results.Json(new ErrorResponse { Error = ErrorResponse.NotFound }, statusCode: 404);
            }

            await audit.WriteAsync(clientId, "secret.delete", name, true, http.RemoteIp(), null, ct);
            return Results.NoContent();
        });

        group.MapGet("/", async (string? prefix, ClaimsPrincipal user, ClientDirectory clients, SecretService secrets, AuditWriter audit, HttpContext http, CancellationToken ct) =>
        {
            prefix ??= "";
            var clientId = user.ClientId();
            var rules = await clients.GetRulesAsync(user.Roles(), ct);
            if (!Authorizer.HasPermission(rules, prefix, Permission.Read))
            {
                await audit.WriteAsync(clientId, "secret.list", prefix, false, http.RemoteIp(), "forbidden", ct);
                return Results.Json(new ErrorResponse { Error = ErrorResponse.Forbidden }, statusCode: StatusCodes.Status403Forbidden);
            }

            var items = await secrets.ListAsync(prefix, ct);
            await audit.WriteAsync(clientId, "secret.list", null, true, http.RemoteIp(), prefix, ct);
            return Results.Ok(items);
        });
    }
}

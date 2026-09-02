using System.Security.Claims;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
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
            if (IfNoneMatchSatisfied(http.Request.Headers.IfNoneMatch, etag))
            {
                await audit.WriteAsync(clientId, "secret.read", name, true, http.RemoteIp(), "not-modified", ct);
                // RFC 9110 15.4.5: a 304 carries the same validator the 200 would have carried.
                http.Response.Headers.ETag = etag;
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

            if (request.Value is null)
            {
                await audit.WriteAsync(clientId, "secret.write", name, false, http.RemoteIp(), "invalid value", ct);
                return Results.BadRequest(new ErrorResponse { Error = ErrorResponse.InvalidRequest, Detail = "value is required (base64)." });
            }

            byte[] value;
            try { value = Convert.FromBase64String(request.Value); }
            catch (FormatException)
            {
                await audit.WriteAsync(clientId, "secret.write", name, false, http.RemoteIp(), "invalid base64", ct);
                return Results.BadRequest(new ErrorResponse { Error = ErrorResponse.InvalidRequest, Detail = "Value must be base64-encoded." });
            }

            int version;
            try
            {
                version = await secrets.SetAsync(name, value, request.ContentType, clientId, ct);
            }
            catch (Exception ex) when (ex is ArgumentException or SecretValidationException or SecretConflictException or SecretNotFoundException)
            {
                await audit.WriteAsync(clientId, "secret.write", name, false, http.RemoteIp(), ex.GetType().Name, ct);
                throw;
            }
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
            catch (Exception ex) when (ex is ArgumentException or SecretValidationException or SecretConflictException or SecretNotFoundException)
            {
                await audit.WriteAsync(clientId, "secret.delete", name, false, http.RemoteIp(), ex.GetType().Name, ct);
                throw;
            }

            await audit.WriteAsync(clientId, "secret.delete", name, true, http.RemoteIp(), null, ct);
            return Results.NoContent();
        });

        group.MapGet("/", async (string? prefix, ClaimsPrincipal user, ClientDirectory clients, SecretService secrets, AuditWriter audit, HttpContext http, CancellationToken ct) =>
        {
            prefix ??= "";
            // The prefix becomes a LIKE pattern and an audit detail; it is not a secret name (it may end mid-segment),
            // so it gets its own bounds check rather than SecretName.IsValid. An empty prefix stays legal.
            if (!IsValidPrefix(prefix)) return Results.BadRequest(new ErrorResponse { Error = ErrorResponse.InvalidRequest, Detail = "Invalid prefix." });
            var clientId = user.ClientId();
            var rules = await clients.GetRulesAsync(user.Roles(), ct);
            if (!Authorizer.HasPermission(rules, prefix, Permission.Read))
            {
                await audit.WriteAsync(clientId, "secret.list", null, false, http.RemoteIp(), prefix, ct);
                return Results.Json(new ErrorResponse { Error = ErrorResponse.Forbidden }, statusCode: StatusCodes.Status403Forbidden);
            }

            var items = await secrets.ListAsync(prefix, ct);
            await audit.WriteAsync(clientId, "secret.list", null, true, http.RemoteIp(), prefix, ct);
            return Results.Ok(items);
        });
    }

    private static bool IsValidPrefix(string prefix)
    {
        if (prefix.Length > SecretName.MaxLength) return false;
        foreach (var c in prefix)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-' or '/')) return false;
        }
        return true;
    }

    /// <summary>True when any If-None-Match tag is "*" or matches the current tag under the weak comparison RFC 9110 prescribes for conditional GETs.</summary>
    private static bool IfNoneMatchSatisfied(StringValues header, string currentTag)
    {
        if (header.Count == 0) return false;
        if (!EntityTagHeaderValue.TryParseList(header, out var tags) || tags is null) return false;
        var current = new EntityTagHeaderValue(currentTag);
        return tags.Any(t => t.Tag == "*" || t.Compare(current, useStrongComparison: false));
    }
}

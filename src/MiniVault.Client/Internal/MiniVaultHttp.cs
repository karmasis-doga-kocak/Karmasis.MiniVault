using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiniVault.Contracts;

namespace MiniVault.Client.Internal;

/// <summary>
/// Thin typed wrapper over an <see cref="HttpClient"/> for the MiniVault HTTP API. It does not own the client's
/// lifetime, <see cref="HttpClient.BaseAddress"/>, or <see cref="HttpClient.Timeout"/> — the caller configures
/// those — and it never manages or attaches an access token itself; every call that needs one takes a
/// <c>bearer</c> parameter.
/// </summary>
internal sealed class MiniVaultHttp
{
    private readonly HttpClient _http;

    public MiniVaultHttp(HttpClient http)
    {
        if (http is null) throw new ArgumentNullException(nameof(http));
        _http = http;
    }

    /// <summary>Exchanges credentials for an access token. A 401 becomes <see cref="MiniVaultAuthException"/>.</summary>
    public async Task<TokenResponse> RequestTokenAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/auth/token")
        {
            Content = JsonContent(new TokenRequest { ClientId = clientId, ClientSecret = clientSecret }),
        };

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) await ThrowForAsync(response).ConfigureAwait(false);
        return (await ReadJsonAsync<TokenResponse>(response).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Fetches a secret. When <paramref name="ifNoneMatch"/> is set, it is sent verbatim as the
    /// <c>If-None-Match</c> header — it is an entity tag as the server produced it, quotes included — and a 304
    /// comes back as <see cref="HttpResult{T}.NotModified"/>.
    /// </summary>
    public async Task<HttpResult<SecretResponse>> GetSecretAsync(string name, string bearer, string? ifNoneMatch, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildSecretPath(name));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (!string.IsNullOrEmpty(ifNoneMatch))
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return new HttpResult<SecretResponse>((int)response.StatusCode, default, ETagOf(response), null);

        if (!response.IsSuccessStatusCode) await ThrowForAsync(response).ConfigureAwait(false);
        var body = await ReadJsonAsync<SecretResponse>(response).ConfigureAwait(false);
        return new HttpResult<SecretResponse>((int)response.StatusCode, body, ETagOf(response), null);
    }

    /// <summary>Writes a secret's value.</summary>
    public async Task<SetSecretResponse> PutSecretAsync(string name, string bearer, SetSecretRequest body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, BuildSecretPath(name))
        {
            Content = JsonContent(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) await ThrowForAsync(response).ConfigureAwait(false);
        return (await ReadJsonAsync<SetSecretResponse>(response).ConfigureAwait(false))!;
    }

    /// <summary>Deletes a secret.</summary>
    public async Task DeleteSecretAsync(string name, string bearer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, BuildSecretPath(name));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) await ThrowForAsync(response).ConfigureAwait(false);
    }

    /// <summary>Lists secrets whose name starts with <paramref name="prefix"/>.</summary>
    public async Task<IReadOnlyList<SecretListItem>> ListAsync(string prefix, string bearer, CancellationToken ct)
    {
        var path = "v1/secrets?prefix=" + Uri.EscapeDataString(prefix ?? "");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) await ThrowForAsync(response).ConfigureAwait(false);
        var items = await ReadJsonAsync<List<SecretListItem>>(response).ConfigureAwait(false);
        return items ?? new List<SecretListItem>();
    }

    /// <summary>
    /// Builds the request path for a secret name. The name may contain <c>/</c> as a segment separator; only each
    /// segment is escaped (<see cref="Uri.EscapeDataString"/>) — the name as a whole is never escaped, so a literal
    /// <c>/</c> in the name keeps acting as a path separator rather than becoming <c>%2F</c>.
    /// </summary>
    private static string BuildSecretPath(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        var segments = name.Split('/');
        for (var i = 0; i < segments.Length; i++) segments[i] = Uri.EscapeDataString(segments[i]);
        return "v1/secrets/" + string.Join("/", segments);
    }

    private static string? ETagOf(HttpResponseMessage response) => response.Headers.ETag?.Tag;

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller asked for this — propagate as the cancellation it is, not as unavailability.
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // Not caused by the caller's token: HttpClient's own timeout expired.
            throw new MiniVaultUnavailableException("The MiniVault request timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new MiniVaultUnavailableException("The MiniVault server could not be reached.", ex);
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return string.IsNullOrEmpty(json) ? default : Json.Deserialize<T>(json);
    }

    private static StringContent JsonContent<T>(T value) => new StringContent(Json.Serialize(value), Encoding.UTF8, "application/json");

    /// <summary>
    /// Maps a non-success response to the matching <see cref="MiniVaultException"/> and throws it: 401 →
    /// <see cref="MiniVaultAuthException"/>, 403 → <see cref="MiniVaultForbiddenException"/>, 404 →
    /// <see cref="MiniVaultNotFoundException"/>, 400/409 → <see cref="MiniVaultRequestException"/>, 429/5xx →
    /// <see cref="MiniVaultUnavailableException"/> (429 is a rate limit, treated as retryable unavailability).
    /// The body is parsed as <see cref="ErrorResponse"/> when present; a missing or unparseable body (405/415 edge
    /// cases) is tolerated and simply yields no error code/detail.
    /// </summary>
    private static async Task ThrowForAsync(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        ErrorResponse? error = null;
        try
        {
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(json)) error = Json.Deserialize<ErrorResponse>(json);
        }
        catch
        {
            // Body missing or not valid JSON — proceed without an error code/detail.
        }

        var message = BuildMessage(status, error);
        switch (status)
        {
            case 401: throw new MiniVaultAuthException(message, status, error?.Error);
            case 403: throw new MiniVaultForbiddenException(message, status, error?.Error);
            case 404: throw new MiniVaultNotFoundException(message, status, error?.Error);
            case 400:
            case 409:
                throw new MiniVaultRequestException(message, status, error?.Error);
            case 429:
                throw new MiniVaultUnavailableException(message, null, status, error?.Error);
            default:
                if (status >= 500) throw new MiniVaultUnavailableException(message, null, status, error?.Error);
                throw new MiniVaultRequestException(message, status, error?.Error);
        }
    }

    private static string BuildMessage(int status, ErrorResponse? error)
    {
        if (error is null) return $"MiniVault request failed with status {status}.";
        return error.Detail is null
            ? $"MiniVault request failed with status {status} ({error.Error})."
            : $"MiniVault request failed with status {status} ({error.Error}): {error.Detail}";
    }
}

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using MiniVault.Client.Internal;
using MiniVault.Client.Tests.TestDoubles;
using MiniVault.Contracts;

namespace MiniVault.Client.Tests.Internal;

public class MiniVaultHttpTests
{
    private static (MiniVaultHttp Http, StubHandler Handler) CreateSut()
    {
        var handler = new StubHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://vault.test/") };
        return (new MiniVaultHttp(httpClient), handler);
    }

    [Fact]
    public async Task RequestTokenAsync_Maps_200_ToTokenResponse()
    {
        var (http, handler) = CreateSut();
        handler.Enqueue(HttpStatusCode.OK, new TokenResponse { AccessToken = "tok", ExpiresIn = 3600 });

        var result = await http.RequestTokenAsync("client", "secret", CancellationToken.None);

        result.AccessToken.ShouldBe("tok");
        result.ExpiresIn.ShouldBe(3600);
    }

    [Fact]
    public async Task RequestTokenAsync_Maps_401_ToAuthException()
    {
        var (http, handler) = CreateSut();
        handler.Enqueue(HttpStatusCode.Unauthorized, new ErrorResponse { Error = ErrorResponse.Unauthorized });

        var ex = await Should.ThrowAsync<MiniVaultAuthException>(() => http.RequestTokenAsync("client", "bad", CancellationToken.None));

        ex.ErrorCode.ShouldBe(ErrorResponse.Unauthorized);
        ex.StatusCode.ShouldBe(401);
    }

    [Fact]
    public async Task GetSecretAsync_Maps_200_ToBody_WithETag()
    {
        var (http, handler) = CreateSut();
        handler.Enqueue(
            HttpStatusCode.OK,
            new SecretResponse { Name = "a", Value = "AQID", Version = 3, UpdatedAt = DateTimeOffset.UtcNow },
            response => response.Headers.ETag = new EntityTagHeaderValue("\"3\""));

        var result = await http.GetSecretAsync("a", "tok", null, CancellationToken.None);

        result.NotModified.ShouldBeFalse();
        result.Body!.Name.ShouldBe("a");
        result.ETag.ShouldBe("\"3\"");
    }

    [Fact]
    public async Task GetSecretAsync_Sends_IfNoneMatch_AndMaps_304_ToNotModified()
    {
        var (http, handler) = CreateSut();
        handler.Enqueue(HttpStatusCode.NotModified, configure: response => response.Headers.ETag = new EntityTagHeaderValue("\"3\""));

        var result = await http.GetSecretAsync("a", "tok", 3, CancellationToken.None);

        result.NotModified.ShouldBeTrue();
        result.ETag.ShouldBe("\"3\"");
        handler.Requests[0].Headers.GetValues("If-None-Match").ShouldContain("\"3\"");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, typeof(MiniVaultForbiddenException))]
    [InlineData(HttpStatusCode.NotFound, typeof(MiniVaultNotFoundException))]
    [InlineData(HttpStatusCode.BadRequest, typeof(MiniVaultRequestException))]
    [InlineData(HttpStatusCode.Conflict, typeof(MiniVaultRequestException))]
    [InlineData(HttpStatusCode.ServiceUnavailable, typeof(MiniVaultUnavailableException))]
    public async Task GetSecretAsync_Maps_ErrorStatus_ToExceptionType_WithStatusCode(HttpStatusCode status, Type expectedType)
    {
        var (http, handler) = CreateSut();
        handler.Enqueue(status, new ErrorResponse { Error = "x", Detail = "detail" });

        var ex = await Should.ThrowAsync<MiniVaultException>(() => http.GetSecretAsync("a", "tok", null, CancellationToken.None));

        ex.ShouldBeOfType(expectedType);
        ex.StatusCode.ShouldBe((int)status);
    }

    [Fact]
    public async Task GetSecretAsync_Maps_429_ToUnavailable()
    {
        var (http, handler) = CreateSut();
        handler.Enqueue((HttpStatusCode)429, new ErrorResponse { Error = "rate_limited" });

        var ex = await Should.ThrowAsync<MiniVaultUnavailableException>(() => http.GetSecretAsync("a", "tok", null, CancellationToken.None));

        ex.StatusCode.ShouldBe(429);
    }

    [Fact]
    public async Task GetSecretAsync_TolerantOfMissingErrorBody()
    {
        var (http, handler) = CreateSut();
        handler.Enqueue(HttpStatusCode.MethodNotAllowed);

        var ex = await Should.ThrowAsync<MiniVaultException>(() => http.GetSecretAsync("a", "tok", null, CancellationToken.None));

        ex.StatusCode.ShouldBe(405);
        ex.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task GetSecretAsync_Maps_HttpRequestException_ToUnavailable_WithInner()
    {
        var (http, handler) = CreateSut();
        var thrown = new HttpRequestException("boom");
        handler.Enqueue(thrown);

        var ex = await Should.ThrowAsync<MiniVaultUnavailableException>(() => http.GetSecretAsync("a", "tok", null, CancellationToken.None));

        ex.InnerException.ShouldBe(thrown);
    }

    [Fact]
    public async Task GetSecretAsync_Maps_Timeout_ToUnavailable()
    {
        var (http, handler) = CreateSut();
        handler.Enqueue(new TaskCanceledException("timeout"));

        var ex = await Should.ThrowAsync<MiniVaultUnavailableException>(() => http.GetSecretAsync("a", "tok", null, CancellationToken.None));

        // HttpClient on some target frameworks re-wraps a handler's TaskCanceledException in its own instance
        // (tied to an internal linked cancellation source), so identity is not guaranteed across TFMs — only type is.
        ex.InnerException.ShouldBeOfType<TaskCanceledException>();
    }

    [Fact]
    public async Task GetSecretAsync_Propagates_UserCancellation_AsOperationCanceled()
    {
        var (http, handler) = CreateSut();
        using var cts = new CancellationTokenSource();
        // Simulates what the real pipeline does when the caller's token fires mid-request: it surfaces as an
        // OperationCanceledException carrying that same (now-cancelled) token.
        handler.Enqueue(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Should.ThrowAsync<OperationCanceledException>(() => http.GetSecretAsync("a", "tok", null, cts.Token));
    }

    [Fact]
    public async Task PutSecretAsync_Sends_Bearer_AndCamelCaseJsonBody()
    {
        var (http, handler) = CreateSut();
        handler.Enqueue(HttpStatusCode.OK, new SetSecretResponse { Version = 2 });

        var result = await http.PutSecretAsync("a", "tok", new SetSecretRequest { Value = "AQID", ContentType = "text/plain" }, CancellationToken.None);

        result.Version.ShouldBe(2);
        handler.Requests[0].Headers.Authorization!.ToString().ShouldBe("Bearer tok");
        handler.RequestBodies[0].ShouldBe("{\"value\":\"AQID\",\"contentType\":\"text/plain\"}");
    }

    [Fact]
    public async Task BuildsPath_ForMultiSegmentName_WithRawSlashes()
    {
        var (http, handler) = CreateSut();
        handler.Enqueue(HttpStatusCode.OK, new SecretResponse { Name = "dataskope/collector/cert", Value = "AQID", Version = 1, UpdatedAt = DateTimeOffset.UtcNow });

        await http.GetSecretAsync("dataskope/collector/cert", "tok", null, CancellationToken.None);

        handler.Requests[0].RequestUri!.AbsolutePath.ShouldBe("/v1/secrets/dataskope/collector/cert");
    }

    [Fact]
    public async Task EscapesSegmentContainingASpace()
    {
        var (http, handler) = CreateSut();
        handler.Enqueue(HttpStatusCode.OK, new SecretResponse { Name = "a b/c", Value = "AQID", Version = 1, UpdatedAt = DateTimeOffset.UtcNow });

        await http.GetSecretAsync("a b/c", "tok", null, CancellationToken.None);

        handler.Requests[0].RequestUri!.AbsoluteUri.ShouldContain("a%20b/c");
    }

    [Fact]
    public async Task DeleteSecretAsync_Sends_Bearer()
    {
        var (http, handler) = CreateSut();
        handler.Enqueue(HttpStatusCode.NoContent);

        await http.DeleteSecretAsync("a", "tok", CancellationToken.None);

        handler.Requests[0].Method.ShouldBe(HttpMethod.Delete);
        handler.Requests[0].Headers.Authorization!.ToString().ShouldBe("Bearer tok");
    }

    [Fact]
    public async Task ListAsync_Maps_200_ToItems()
    {
        var (http, handler) = CreateSut();
        handler.Enqueue(HttpStatusCode.OK, new List<SecretListItem>
        {
            new SecretListItem { Name = "a", Version = 1, UpdatedAt = DateTimeOffset.UtcNow },
        });

        var items = await http.ListAsync("a", "tok", CancellationToken.None);

        items.Count.ShouldBe(1);
        items[0].Name.ShouldBe("a");
        handler.Requests[0].RequestUri!.PathAndQuery.ShouldBe("/v1/secrets?prefix=a");
    }
}

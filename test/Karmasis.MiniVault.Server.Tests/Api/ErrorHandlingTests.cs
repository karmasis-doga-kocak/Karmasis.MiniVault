using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Karmasis.MiniVault.Contracts;
using Karmasis.MiniVault.Server.Api;
using Karmasis.MiniVault.Server.Secrets;

namespace Karmasis.MiniVault.Server.Tests.Api;

/// <summary>
/// Exercises <see cref="ErrorHandling.UseMiniVaultErrorHandling"/> directly against a minimal in-test app, for
/// exception-to-status mappings that are impractical to trigger honestly through the full HTTP stack (e.g. a
/// genuine optimistic-concurrency conflict).
/// </summary>
public class ErrorHandlingTests
{
    private static async Task<(HttpClient Client, WebApplication App)> BuildAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.UseMiniVaultErrorHandling();
        app.MapGet("/conflict", (HttpContext _) => throw new SecretConflictException("some/secret"));
        app.MapGet("/bad-request", (HttpContext _) => throw new BadHttpRequestException("bad body"));
        app.MapGet("/boom", (HttpContext _) => throw new InvalidOperationException("boom"));
        await app.StartAsync();
        return (app.GetTestClient(), app);
    }

    [Fact]
    public async Task SecretConflictException_Maps_To_409()
    {
        var (client, app) = await BuildAppAsync();
        await using var _ = app;

        var response = await client.GetAsync("/conflict");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.ShouldBe(ErrorResponse.Conflict);
    }

    [Fact]
    public async Task BadHttpRequestException_Maps_To_400()
    {
        var (client, app) = await BuildAppAsync();
        await using var _ = app;

        var response = await client.GetAsync("/bad-request");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.ShouldBe(ErrorResponse.InvalidRequest);
    }

    [Fact]
    public async Task UnhandledException_Maps_To_500_InternalError()
    {
        var (client, app) = await BuildAppAsync();
        await using var _ = app;

        var response = await client.GetAsync("/boom");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.ShouldBe("internal_error");
    }
}

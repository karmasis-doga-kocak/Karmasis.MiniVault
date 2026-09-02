using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MiniVault.Contracts;
using MiniVault.Server.Cli;
using MiniVault.Server.Keys;

namespace MiniVault.Server.Tests.Api;

public class EndToEndScenarioTests : IClassFixture<ApiTestFixture>
{
    private readonly ApiTestFixture _f;
    public EndToEndScenarioTests(ApiTestFixture f) => _f = f;

    private async Task<string> Cli(params string[] args)
    {
        var output = new StringWriter();
        var code = await CliApp.RunAsync([.. args, "--ConnectionStrings:MiniVault", _f.Db.ConnectionString], output, s => s.AddSingleton<IMasterKeyProvider>(_f.Provider));
        code.ShouldBe(0, output.ToString());
        return output.ToString();
    }

    [Fact]
    public async Task Collector_Lifecycle_Through_Cli_And_Http()
    {
        // 1. operator: role + client through the CLI
        await Cli("role", "add", "e2e-writer");
        await Cli("role", "grant", "e2e-writer", "--scope", "e2e/", "--permission", "write");
        var addOutput = await Cli("client", "add", "e2e-client", "--role", "e2e-writer");
        var secret = addOutput.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith("Client secret:"))["Client secret:".Length..].Trim();

        // 2. service: token
        var http = _f.Factory.CreateClient();
        var tokenResponse = await http.PostAsJsonAsync("/v1/auth/token", new TokenRequest { ClientId = "e2e-client", ClientSecret = secret });
        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = (await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>())!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        // 3. put a PFX-like blob, get it back
        var pfx = new byte[4096]; new Random(42).NextBytes(pfx);
        var put = await http.PutAsJsonAsync("/v1/secrets/e2e/collector/cert", new SetSecretRequest { Value = Convert.ToBase64String(pfx), ContentType = "application/x-pkcs12" });
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await put.Content.ReadFromJsonAsync<SetSecretResponse>())!.Version.ShouldBe(1);
        var get = await http.GetAsync("/v1/secrets/e2e/collector/cert");
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await get.Content.ReadFromJsonAsync<SecretResponse>())!;
        Convert.FromBase64String(body.Value).ShouldBe(pfx);
        body.ContentType.ShouldBe("application/x-pkcs12");
        get.Headers.ETag!.Tag.ShouldBe("\"1\"");

        // 4. rotate the DEK from the CLI; old secret still readable, new write uses version 2.
        // The CLI's rotate-dek runs against a separate, short-lived host, so it cannot refresh the running
        // server's singleton DataKeyRing; per docs/operations.md that requires a server restart. This in-process
        // test cannot restart the server, so it reloads the ring directly to reproduce what a restart would do.
        await Cli("rotate-dek");
        await _f.Factory.Services.GetRequiredService<DataKeyRing>().ReloadAsync(CancellationToken.None);
        (await http.GetAsync("/v1/secrets/e2e/collector/cert")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await http.PutAsJsonAsync("/v1/secrets/e2e/collector/cert", new SetSecretRequest { Value = Convert.ToBase64String(pfx) })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var ctx = _f.Db.CreateContext())
            (await ctx.Secrets.SingleAsync(s => s.Name == "e2e/collector/cert")).DekVersion.ShouldBe(2);

        // 5. delete, then 404
        (await http.DeleteAsync("/v1/secrets/e2e/collector/cert")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await http.GetAsync("/v1/secrets/e2e/collector/cert")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // 6. audit trail
        var audit = await _f.AuditAsync();
        var mine = audit.Where(a => a.ClientId == "e2e-client").Select(a => a.Action).ToList();
        mine.ShouldContain("token"); mine.ShouldContain("secret.write"); mine.ShouldContain("secret.read"); mine.ShouldContain("secret.delete");
    }
}

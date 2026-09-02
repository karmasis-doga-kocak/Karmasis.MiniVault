using System.Net;
using System.Net.Http.Json;
using System.Text;
using MiniVault.Contracts;

namespace MiniVault.Server.Tests.Api;

public class SecretEndpointTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public async Task Put_Get_RoundTrip_WithETag_And304()
    {
        var webui = await fixture.ClientWithTokenAsync("webui");
        var collector = await fixture.ClientWithTokenAsync("collector");
        var bytes = Encoding.UTF8.GetBytes("cert-bytes");
        const string name = "dataskope/collector/cert";

        var putResponse = await webui.PutAsJsonAsync($"/v1/secrets/{name}", new SetSecretRequest { Value = Convert.ToBase64String(bytes), ContentType = "text/plain" });
        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var putBody = await putResponse.Content.ReadFromJsonAsync<SetSecretResponse>();
        putBody!.Version.ShouldBe(1);

        var getResponse = await collector.GetAsync($"/v1/secrets/{name}");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        getResponse.Headers.ETag!.Tag.ShouldBe("\"1\"");
        var getBody = await getResponse.Content.ReadFromJsonAsync<SecretResponse>();
        Convert.FromBase64String(getBody!.Value).ShouldBe(bytes);

        var conditional = new HttpRequestMessage(HttpMethod.Get, $"/v1/secrets/{name}");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", "\"1\"");
        var notModified = await collector.SendAsync(conditional);
        notModified.StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task Get_WithoutToken_401()
    {
        var http = fixture.Factory.CreateClient();

        var response = await http.GetAsync("/v1/secrets/dataskope/whatever");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.ShouldBe(ErrorResponse.Unauthorized);
    }

    [Fact]
    public async Task Collector_CanRead_ButPutIs403()
    {
        var webui = await fixture.ClientWithTokenAsync("webui");
        var collector = await fixture.ClientWithTokenAsync("collector");
        const string name = "dataskope/collector/readable";
        await webui.PutAsJsonAsync($"/v1/secrets/{name}", new SetSecretRequest { Value = Convert.ToBase64String("v"u8.ToArray()) });

        var getResponse = await collector.GetAsync($"/v1/secrets/{name}");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var putResponse = await collector.PutAsJsonAsync($"/v1/secrets/{name}", new SetSecretRequest { Value = Convert.ToBase64String("v2"u8.ToArray()) });
        putResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var body = await putResponse.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.ShouldBe(ErrorResponse.Forbidden);
    }

    [Fact]
    public async Task Collector_CannotRead_OutsideScope_403()
    {
        var collector = await fixture.ClientWithTokenAsync("collector");

        var response = await collector.GetAsync("/v1/secrets/webui/x");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_Unknown_404()
    {
        var webui = await fixture.ClientWithTokenAsync("webui");

        var response = await webui.GetAsync("/v1/secrets/dataskope/does-not-exist");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.ShouldBe(ErrorResponse.NotFound);
    }

    [Fact]
    public async Task Put_InvalidName_400()
    {
        var webui = await fixture.ClientWithTokenAsync("webui");

        var response = await webui.PutAsJsonAsync("/v1/secrets/dataskope/bad%20name", new SetSecretRequest { Value = Convert.ToBase64String("v"u8.ToArray()) });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_InvalidBase64_400()
    {
        var webui = await fixture.ClientWithTokenAsync("webui");

        var response = await webui.PutAsJsonAsync("/v1/secrets/dataskope/invalid-b64", new SetSecretRequest { Value = "not-valid-base64!!" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_TooLarge_400()
    {
        var webui = await fixture.ClientWithTokenAsync("webui");
        var tooLarge = new byte[1_048_577];
        const string name = "dataskope/too-large";

        var response = await webui.PutAsJsonAsync($"/v1/secrets/{name}", new SetSecretRequest { Value = Convert.ToBase64String(tooLarge) });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var audits = await fixture.AuditAsync();
        audits.ShouldContain(a => a.Action == "secret.write" && !a.Success && a.SecretName == name);
    }

    [Fact]
    public async Task Put_MissingValue_400()
    {
        var webui = await fixture.ClientWithTokenAsync("webui");
        const string name = "dataskope/missing-value";
        var content = new StringContent("{\"contentType\":\"text/plain\"}", Encoding.UTF8, "application/json");

        var response = await webui.PutAsync($"/v1/secrets/{name}", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.ShouldBe(ErrorResponse.InvalidRequest);

        var getResponse = await webui.GetAsync($"/v1/secrets/{name}");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Put_NullValue_400()
    {
        var webui = await fixture.ClientWithTokenAsync("webui");
        const string name = "dataskope/null-value";
        var content = new StringContent("{\"value\":null}", Encoding.UTF8, "application/json");

        var response = await webui.PutAsync($"/v1/secrets/{name}", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        body!.Error.ShouldBe(ErrorResponse.InvalidRequest);

        var getResponse = await webui.GetAsync($"/v1/secrets/{name}");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_ThenGet_404_And204()
    {
        var webui = await fixture.ClientWithTokenAsync("webui");
        const string name = "dataskope/to-delete";
        await webui.PutAsJsonAsync($"/v1/secrets/{name}", new SetSecretRequest { Value = Convert.ToBase64String("v"u8.ToArray()) });

        var deleteResponse = await webui.DeleteAsync($"/v1/secrets/{name}");
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var getResponse = await webui.GetAsync($"/v1/secrets/{name}");
        getResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var deleteAgain = await webui.DeleteAsync($"/v1/secrets/{name}");
        deleteAgain.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_ByPrefix_ReturnsItems_AndRequiresReadOnPrefix()
    {
        var webui = await fixture.ClientWithTokenAsync("webui");
        var collector = await fixture.ClientWithTokenAsync("collector");
        const string name = "dataskope/list-test/item";
        await webui.PutAsJsonAsync($"/v1/secrets/{name}", new SetSecretRequest { Value = Convert.ToBase64String("v"u8.ToArray()) });

        var listResponse = await collector.GetAsync("/v1/secrets?prefix=dataskope/list-test/");
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items = await listResponse.Content.ReadFromJsonAsync<List<SecretListItem>>();
        items!.ShouldContain(i => i.Name == name);

        var forbidden = await collector.GetAsync("/v1/secrets?prefix=");
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Put_Twice_IncrementsVersion()
    {
        var webui = await fixture.ClientWithTokenAsync("webui");
        const string name = "dataskope/versioned";

        var first = await webui.PutAsJsonAsync($"/v1/secrets/{name}", new SetSecretRequest { Value = Convert.ToBase64String("v1"u8.ToArray()) });
        var second = await webui.PutAsJsonAsync($"/v1/secrets/{name}", new SetSecretRequest { Value = Convert.ToBase64String("v2"u8.ToArray()) });

        (await first.Content.ReadFromJsonAsync<SetSecretResponse>())!.Version.ShouldBe(1);
        (await second.Content.ReadFromJsonAsync<SetSecretResponse>())!.Version.ShouldBe(2);
    }

    [Fact]
    public async Task Audit_RecordsReadAndWrite()
    {
        var webui = await fixture.ClientWithTokenAsync("webui");
        const string name = "dataskope/audited";
        await webui.PutAsJsonAsync($"/v1/secrets/{name}", new SetSecretRequest { Value = Convert.ToBase64String("v"u8.ToArray()) });
        await webui.GetAsync($"/v1/secrets/{name}");

        var audits = await fixture.AuditAsync();

        audits.ShouldContain(a => a.Action == "secret.write" && a.ClientId == "webui" && a.Success);
        audits.ShouldContain(a => a.Action == "secret.read" && a.ClientId == "webui" && a.Success);
    }
}

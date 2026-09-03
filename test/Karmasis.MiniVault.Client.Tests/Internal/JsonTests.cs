using Karmasis.MiniVault.Client.Internal;
using Karmasis.MiniVault.Contracts;

namespace Karmasis.MiniVault.Client.Tests.Internal;

public class JsonTests
{
    [Fact]
    public void Serializes_CamelCase_AndOmitsNulls()
    {
        var json = Json.Serialize(new SetSecretRequest { Value = "AQID", ContentType = null });
        json.ShouldBe("{\"value\":\"AQID\"}");
    }

    [Fact]
    public void Deserializes_CaseInsensitive()
    {
        var r = Json.Deserialize<SecretResponse>("{\"Name\":\"a/b\",\"value\":\"AQID\",\"contentType\":\"text/plain\",\"version\":3,\"updatedAt\":\"2026-09-02T12:00:00+00:00\"}");
        r.Name.ShouldBe("a/b"); r.Value.ShouldBe("AQID"); r.ContentType.ShouldBe("text/plain"); r.Version.ShouldBe(3);
        r.UpdatedAt.ShouldBe(new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero));
    }
}

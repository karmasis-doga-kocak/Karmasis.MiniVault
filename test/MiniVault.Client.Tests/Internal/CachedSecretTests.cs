using MiniVault.Client.Internal;

namespace MiniVault.Client.Tests.Internal;

public class CachedSecretTests
{
    [Fact]
    public void Constructor_CopiesValue()
    {
        var source = new byte[] { 1, 2, 3 };
        var cached = new CachedSecret("name", source, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        source[0] = 99;

        cached.Value.ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void ToSecret_ReturnsCopy()
    {
        var cached = new CachedSecret("name", new byte[] { 1, 2, 3 }, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var secret = cached.ToSecret();
        secret.Value[0] = 99;

        cached.Value.ShouldBe(new byte[] { 1, 2, 3 });
    }
}

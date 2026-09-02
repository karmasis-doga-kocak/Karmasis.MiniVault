using MiniVault.Server.Secrets;

namespace MiniVault.Server.Tests.Secrets;

public class SecretNameTests
{
    [Theory]
    [InlineData("a", true)] [InlineData("dataskope/collector/cert", true)] [InlineData("a.b-c_d", true)]
    [InlineData("", false)] [InlineData(null, false)] [InlineData("/a", false)] [InlineData("a/", false)] [InlineData("a//b", false)]
    [InlineData("a b", false)] [InlineData("a\\b", false)] [InlineData("ünïcode", false)]
    public void IsValid(string? name, bool expected) => SecretName.IsValid(name).ShouldBe(expected);

    [Fact]
    public void IsValid_RejectsOverMaxLength() => SecretName.IsValid(new string('a', SecretName.MaxLength + 1)).ShouldBeFalse();
}

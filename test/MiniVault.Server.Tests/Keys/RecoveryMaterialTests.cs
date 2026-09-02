using MiniVault.Server.Data.Entities;
using MiniVault.Server.Keys;

namespace MiniVault.Server.Tests.Keys;

public class RecoveryMaterialTests
{
    [Fact]
    public void Single_GeneratesOnePart_ThatReconstructs()
    {
        var material = RecoveryMaterial.Generate(RecoveryMode.Single);

        material.Key.Length.ShouldBe(32);
        material.Parts.Count.ShouldBe(1);
        material.Shares.ShouldBeNull();
        material.Threshold.ShouldBeNull();
        RecoveryMaterial.Reconstruct(RecoveryMode.Single, material.Parts).ShouldBe(material.Key);
    }

    [Fact]
    public void Shamir_GeneratesShares_AnyThresholdReconstructs()
    {
        var material = RecoveryMaterial.Generate(RecoveryMode.Shamir, shares: 3, threshold: 2);

        material.Parts.Count.ShouldBe(3);
        material.Shares.ShouldBe(3);
        material.Threshold.ShouldBe(2);
        RecoveryMaterial.Reconstruct(RecoveryMode.Shamir, [material.Parts[0], material.Parts[2]]).ShouldBe(material.Key);
        RecoveryMaterial.Reconstruct(RecoveryMode.Shamir, material.Parts).ShouldBe(material.Key);
    }

    [Fact]
    public void Shamir_BelowThreshold_DoesNotReconstruct()
    {
        var material = RecoveryMaterial.Generate(RecoveryMode.Shamir, shares: 3, threshold: 3);

        RecoveryMaterial.Reconstruct(RecoveryMode.Shamir, [material.Parts[0], material.Parts[1]]).ShouldNotBe(material.Key);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    public void Shamir_InvalidParameters_Throw(int shares, int threshold) =>
        Should.Throw<ArgumentOutOfRangeException>(() => RecoveryMaterial.Generate(RecoveryMode.Shamir, shares, threshold));

    [Theory]
    [InlineData("not-base64")]
    [InlineData("AAAA")]
    public void Reconstruct_Single_InvalidPart_Throws(string part) =>
        Should.Throw<FormatException>(() => RecoveryMaterial.Reconstruct(RecoveryMode.Single, [part]));

    [Fact]
    public void Reconstruct_Single_RequiresExactlyOnePart() =>
        Should.Throw<ArgumentException>(() => RecoveryMaterial.Reconstruct(RecoveryMode.Single, ["a", "b"]));

    [Fact]
    public void Reconstruct_NullOrBlankPart_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => RecoveryMaterial.Reconstruct(RecoveryMode.Single, [null!]));
        Should.Throw<ArgumentException>(() => RecoveryMaterial.Reconstruct(RecoveryMode.Shamir, ["AQ==", " "]));
    }
}

using System.Security.Cryptography;
using Karmasis.Cryptography.Keys;
using MiniVault.Server.Keys;

namespace MiniVault.Server.Tests.Keys;

public class KeyHierarchyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateDataKey_WrapsSameDekForMasterAndRecovery()
    {
        var kek = KeyGenerator.GenerateKey();
        var recovery = KeyGenerator.GenerateKey();

        var key = KeyHierarchy.CreateDataKey(3, kek, recovery, Now);

        key.Version.ShouldBe(3);
        key.IsActive.ShouldBeFalse();
        key.CreatedAt.ShouldBe(Now);
        var dekA = KeyHierarchy.UnwrapWithMaster(key, kek);
        var dekB = KeyHierarchy.UnwrapWithRecovery(key, recovery);
        dekA.Length.ShouldBe(32);
        dekA.ShouldBe(dekB);
        key.WrappedByMaster.ShouldNotBe(key.WrappedByRecovery);
    }

    [Fact]
    public void UnwrapWithMaster_WrongKek_Throws()
    {
        var key = KeyHierarchy.CreateDataKey(1, KeyGenerator.GenerateKey(), KeyGenerator.GenerateKey(), Now);

        Should.Throw<CryptographicException>(() => KeyHierarchy.UnwrapWithMaster(key, KeyGenerator.GenerateKey()));
    }

    [Fact]
    public void RewrapWithMaster_ChangesOnlyMasterWrapping()
    {
        var kek = KeyGenerator.GenerateKey();
        var recovery = KeyGenerator.GenerateKey();
        var key = KeyHierarchy.CreateDataKey(1, kek, recovery, Now);
        var dek = KeyHierarchy.UnwrapWithMaster(key, kek);
        var recoveryWrappedBefore = key.WrappedByRecovery.ToArray();
        var newKek = KeyGenerator.GenerateKey();

        KeyHierarchy.RewrapWithMaster(key, dek, newKek);

        KeyHierarchy.UnwrapWithMaster(key, newKek).ShouldBe(dek);
        Should.Throw<CryptographicException>(() => KeyHierarchy.UnwrapWithMaster(key, kek));
        key.WrappedByRecovery.ShouldBe(recoveryWrappedBefore);
    }
}

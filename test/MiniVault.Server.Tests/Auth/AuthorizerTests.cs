using MiniVault.Server.Auth;
using MiniVault.Server.Data.Entities;

namespace MiniVault.Server.Tests.Auth;

public class AuthorizerTests
{
    private static RoleRule Rule(string scope, Permission p) => new() { RoleName = "r", Scope = scope, Permission = p };

    [Theory]
    [InlineData("dataskope/", "dataskope/collector/cert", Permission.Read, Permission.Read, true)]
    [InlineData("dataskope/", "dataskope/collector/cert", Permission.Write, Permission.Read, true)]   // Write includes Read
    [InlineData("dataskope/", "dataskope/collector/cert", Permission.Read, Permission.Write, false)]
    [InlineData("dataskope/collector/", "dataskope/webui/x", Permission.Write, Permission.Read, false)]
    [InlineData("", "anything", Permission.Read, Permission.Read, true)]                             // empty scope = everything
    [InlineData("Dataskope/", "dataskope/x", Permission.Write, Permission.Read, false)]              // ordinal, case-sensitive
    public void HasPermission_PrefixAndLevel(string scope, string name, Permission granted, Permission required, bool expected)
        => Authorizer.HasPermission([Rule(scope, granted)], name, required).ShouldBe(expected);

    [Fact]
    public void HasPermission_AnyMatchingRuleSuffices()
        => Authorizer.HasPermission([Rule("a/", Permission.Read), Rule("b/", Permission.Write)], "b/c", Permission.Write).ShouldBeTrue();

    [Fact]
    public void HasPermission_NoRules_IsFalse()
        => Authorizer.HasPermission([], "x", Permission.Read).ShouldBeFalse();
}

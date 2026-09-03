using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Karmasis.MiniVault.Server.Api;
using Karmasis.MiniVault.Server.Cli;
using Karmasis.MiniVault.Server.Tests.Api;

namespace Karmasis.MiniVault.Server.Tests.Docs;

/// <summary>
/// Keeps the prose honest. Documentation drifts silently: a command is renamed, a route moves, and the only thing
/// that notices is an operator following an example that no longer works. These tests read the shipped Markdown and
/// check the two things in it that are mechanically checkable — the CLI command names and the API paths — against
/// the code that defines them.
/// </summary>
public class DocsConsistencyTests
{
    /// <summary>The docs that describe the server's own interface. Everything else (deployment notes) is prose.</summary>
    private static readonly string[] DocumentPaths = ["README.md", "docs/operations.md", "docs/client.md"];

    /// <summary><c>minivault &lt;word&gt;</c> — the word right after the binary name, in an example or in prose.
    /// Case-sensitive on purpose: "MiniVault initialized." in a sample output is not a command invocation.</summary>
    private static readonly Regex CommandReference = new(@"\bminivault(?:\.exe)?\s+([a-z][a-z-]*)", RegexOptions.Compiled);

    /// <summary>Any <c>/v1/...</c> path, with or without a route parameter or a query string.</summary>
    private static readonly Regex ApiPath = new(@"/v1/[A-Za-z0-9/{}?=*-]+", RegexOptions.Compiled);

    /// <summary>Not a subcommand — running the binary with no command starts the server — but the docs are allowed
    /// to name it, because that is what the process does.</summary>
    private const string ServerPseudoCommand = "serve";

    [Fact]
    public void EveryDocumentedCommandExists()
    {
        var known = new HashSet<string>(CliApp.CommandNames, StringComparer.Ordinal) { ServerPseudoCommand };

        var unknown = ReadDocuments()
            .SelectMany(doc => CommandReference.Matches(doc.Text).Select(m => (doc.Path, Command: m.Groups[1].Value)))
            .Where(x => !known.Contains(x.Command))
            .Distinct()
            .ToList();

        unknown.ShouldBeEmpty(
            $"The docs invoke commands that do not exist: {string.Join(", ", unknown.Select(x => $"'minivault {x.Command}' in {x.Path}"))}. " +
            "Either add the command or rewrite the sentence so it does not read as an invocation.");
    }

    [Fact]
    public void EveryDocumentedApiPathIsRouted()
    {
        var routed = ApiExtensions.RoutePatterns.Select(NormalizePath).ToHashSet(StringComparer.Ordinal);

        var unrouted = ReadDocuments()
            .SelectMany(doc => ApiPath.Matches(doc.Text).Select(m => (doc.Path, Raw: m.Value, Normalized: NormalizePath(m.Value))))
            .Where(x => !routed.Any(prefix => x.Normalized == prefix || x.Normalized.StartsWith(prefix + "/", StringComparison.Ordinal)))
            .Distinct()
            .ToList();

        unrouted.ShouldBeEmpty(
            $"The docs use API paths that no route serves: {string.Join(", ", unrouted.Select(x => $"'{x.Raw}' in {x.Path}"))}. " +
            $"Known routes: {string.Join(", ", routed)}.");
    }

    /// <summary>Drops a query string and any route-parameter segment, then the trailing slash, so
    /// <c>/v1/secrets/{**name}</c>, <c>/v1/secrets/</c> and <c>/v1/secrets?prefix=</c> all reduce to
    /// <c>/v1/secrets</c>.</summary>
    private static string NormalizePath(string path)
    {
        var withoutQuery = path.Split('?')[0];
        var segments = withoutQuery.Split('/').TakeWhile(s => !s.StartsWith('{'));
        return string.Join('/', segments).TrimEnd('/');
    }

    private static IEnumerable<(string Path, string Text)> ReadDocuments()
    {
        var root = RepositoryRoot();
        foreach (var relative in DocumentPaths)
        {
            var full = Path.Combine(root, relative);
            File.Exists(full).ShouldBeTrue($"Expected documentation file {relative} under {root}.");
            yield return (relative, File.ReadAllText(full));
        }
    }

    /// <summary>Walks up from the test binary until the solution file appears; the tests run from
    /// <c>bin/Debug/net10.0</c>, which is four levels below the repository root.</summary>
    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Karmasis.MiniVault.sln")))
            directory = directory.Parent;

        directory.ShouldNotBeNull($"Karmasis.MiniVault.sln was not found above {AppContext.BaseDirectory}.");
        return directory.FullName;
    }
}

/// <summary>
/// <see cref="ApiExtensions.RoutePatterns"/> is a hand-written list, and a hand-written list is only useful while it
/// is true. This compares it with the endpoints the running application actually maps.
/// </summary>
public class RoutePatternsTests(ApiTestFixture fixture) : IClassFixture<ApiTestFixture>
{
    [Fact]
    public void RoutePatternsMatchTheMappedEndpoints()
    {
        var mapped = fixture.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText!)
            .ToHashSet(StringComparer.Ordinal);

        mapped.ShouldBe(ApiExtensions.RoutePatterns.ToHashSet(StringComparer.Ordinal), ignoreOrder: true);
    }
}

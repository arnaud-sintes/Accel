using Glaude.Metrics;
using Microsoft.AspNetCore.Builder;

namespace Glaude.Server;

/// <summary>
/// Phase UI-D: the single <c>GET /roots/tree</c> route. Thin by design - all disk
/// enumeration, root attribution, live-state merging, and caching lives in
/// <see cref="RootsTreeBuilder"/> (unit-testable on its own); this class only wires that
/// builder to the configured roots (see <see cref="EventServer.Roots"/>) and the shared
/// <see cref="SessionState"/>, and never fails the request - <see cref="RootsTreeBuilder.Build"/>
/// itself never throws, so there is nothing left for this handler to guard against.
/// </summary>
public static class RootsTreeRoute
{
    public static void Map(WebApplication app, string[] roots, SessionState state, RootsTreeBuilder builder, string? projectsDirOverride = null)
    {
        app.MapGet("/roots/tree", () => Results.Json(
            builder.Build(roots, state, projectsDirOverride, RootFoldersConfig.LoadFull().Sessions)));
    }
}

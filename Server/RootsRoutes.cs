using Microsoft.AspNetCore.Builder;

namespace Accel.Server;

/// <summary>
/// Phase UI-C: the single <c>GET /roots</c> route, exposing the configured root folder list
/// (see <see cref="RootFoldersConfig"/>) read-only, verbatim, over the same loopback-only
/// server used by the Phase 3 event routes and Phase 3d's <see cref="StateQueryRoutes"/>.
///
/// The list is loaded once (at server startup - see <see cref="EventServer"/>) and handed to
/// <see cref="Map"/> as a plain array; this route never re-reads the config file and never
/// fails - a missing/malformed config degrades to <c>[]</c> long before this route runs (see
/// <see cref="RootFoldersConfig.Load()"/>), so this handler itself has nothing that can throw.
/// </summary>
public static class RootsRoutes
{
    public static void Map(WebApplication app, string[] roots)
    {
        string[] snapshot = roots ?? Array.Empty<string>();
        app.MapGet("/roots", () => Results.Json(snapshot));
    }
}

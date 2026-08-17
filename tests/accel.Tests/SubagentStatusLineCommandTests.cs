using Accel.Cli;
using Accel.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Accel.Tests;

/// <summary>
/// Phase 3c: <see cref="SubagentStatusLineCommand"/> must always exit 0 having printed
/// nothing to stdout, regardless of stdin content or server reachability, and must forward
/// whatever it read on stdin to `/events/subagent-status-line`.
/// </summary>
public class SubagentStatusLineCommandTests
{
    [Theory]
    [InlineData("""{"tasks":[{"id":"t1","name":"agent"}]}""")]
    [InlineData("this is not { json at all")]
    [InlineData("")]
    public async Task RunAsync_NeverThrowsAndNeverWritesStdout_ServerUnreachable(string stdinPayload)
    {
        // No server listening on this port - exercises the "server down" failure path.
        const int unreachablePort = 40999;

        var originalOut = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);

        int exitCode;
        try
        {
            using var reader = new StringReader(stdinPayload);
            exitCode = await SubagentStatusLineCommand.RunAsync(unreachablePort, reader);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, captured.ToString());
    }

    [Fact]
    public async Task RunAsync_NeverWritesStdout_EvenWhenServerIsUp()
    {
        var app = EventServer.Build(0);
        await app.StartAsync();
        try
        {
            int port = GetBoundPort(app);

            var originalOut = Console.Out;
            var captured = new StringWriter();
            Console.SetOut(captured);

            int exitCode;
            try
            {
                // No "tasks" field, so the server side (EventPrinter.PrintSubagentStatusLine)
                // is also guaranteed to print nothing here - isolating this assertion to
                // SubagentStatusLineCommand's own stdout behaviour rather than the server's.
                using var reader = new StringReader("""{"hook_event_name":"SubagentStatusLine"}""");
                exitCode = await SubagentStatusLineCommand.RunAsync(port, reader);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, captured.ToString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunAsync_PostsStdinPayload_ToSubagentStatusLineRoute()
    {
        string dumpDir = Path.Combine(Path.GetTempPath(), "accel-tests-" + Guid.NewGuid().ToString("N"));

        var app = EventServer.Build(0, dumpDir);
        await app.StartAsync();
        try
        {
            int port = GetBoundPort(app);
            const string payload = """{"tasks":[{"id":"probe-task","name":"probe"}]}""";

            using var reader = new StringReader(payload);
            int exitCode = await SubagentStatusLineCommand.RunAsync(port, reader);

            Assert.Equal(0, exitCode);

            // RunAsync awaits the POST response, so by the time it returns the server has
            // already handled (and, with --dump-raw enabled, captured) the request.
            Assert.True(Directory.Exists(dumpDir), "expected the dump-raw directory to have been created");
            string[] files = Directory.GetFiles(dumpDir);
            Assert.Single(files);
            string content = await File.ReadAllTextAsync(files[0]);
            Assert.Contains("probe-task", content);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();

            try
            {
                if (Directory.Exists(dumpDir))
                {
                    Directory.Delete(dumpDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task RunAsync_ReturnsZero_ForEmptyStdin()
    {
        var app = EventServer.Build(0);
        await app.StartAsync();
        try
        {
            int port = GetBoundPort(app);

            using var reader = new StringReader(string.Empty);
            int exitCode = await SubagentStatusLineCommand.RunAsync(port, reader);

            Assert.Equal(0, exitCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static int GetBoundPort(WebApplication app)
    {
        var addressesFeature = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();

        string address = addressesFeature!.Addresses.First();
        return new Uri(address).Port;
    }
}

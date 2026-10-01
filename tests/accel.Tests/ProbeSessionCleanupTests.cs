namespace Accel.Tests;

using System;
using System.IO;
using Accel.Orchestration;
using Xunit;

public class ProbeSessionCleanupTests : IDisposable
{
    private const string WorkingDirectory = @"C:\projects\some-root";
    private const string ProjectFolder = "C--projects-some-root";
    private const string ProbeContent =
        """{"type":"system","subtype":"local_command","content":"<command-name>/model</command-name>"}""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"accel-probe-cleanup-{Guid.NewGuid():N}");

    public ProbeSessionCleanupTests() => Directory.CreateDirectory(Path.Combine(_root, ProjectFolder));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, ProjectFolder, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void NewModelOnlyTranscript_IsDeleted()
    {
        var before = ProbeSessionCleanup.Snapshot(WorkingDirectory, _root);
        var probe = Write("a.jsonl", ProbeContent);

        ProbeSessionCleanup.DeleteProbeSessions(WorkingDirectory, before, _root);

        Assert.False(File.Exists(probe));
    }

    [Fact]
    public void TranscriptThatExistedBeforeTheProbe_IsKept()
    {
        var existing = Write("old.jsonl", ProbeContent);
        var before = ProbeSessionCleanup.Snapshot(WorkingDirectory, _root);

        ProbeSessionCleanup.DeleteProbeSessions(WorkingDirectory, before, _root);

        Assert.True(File.Exists(existing));
    }

    [Fact]
    public void NewTranscriptWithConversation_IsKept()
    {
        var before = ProbeSessionCleanup.Snapshot(WorkingDirectory, _root);
        var real = Write("real.jsonl", ProbeContent + "\n" + """{"type":"user","message":"hello"}""");

        ProbeSessionCleanup.DeleteProbeSessions(WorkingDirectory, before, _root);

        Assert.True(File.Exists(real));
    }

    [Fact]
    public void MissingProjectFolder_IsNotAnError()
    {
        var before = ProbeSessionCleanup.Snapshot(@"C:\nowhere", _root);

        ProbeSessionCleanup.DeleteProbeSessions(@"C:\nowhere", before, _root);
    }
}

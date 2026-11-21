using D2NG.Core.Tracing;
using System;
using System.IO;

namespace D2NG.Trace.Tests;

internal sealed class TempRoot : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "d2trace-tests-" + Guid.NewGuid().ToString("N"));

    public TraceRecorder Recorder(int bufferBytes = 64 * 1024, TimeSpan? digestInterval = null)
        => new(new TraceRecorderOptions
        {
            Root = Path,
            BufferBytes = bufferBytes,
            DigestInterval = digestInterval ?? TimeSpan.FromSeconds(5),
            BackgroundWriter = false,
        });

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

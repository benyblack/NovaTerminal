using NovaTerminal.ExternalSuites.Vttest;

namespace NovaTerminal.ExternalSuites.NativeSsh;

public sealed class NativeSshTranscriptDriver
{
    private readonly RecWriter _writer;

    public NativeSshTranscriptDriver(RecWriter writer)
    {
        _writer = writer;
    }

    public async Task ExecuteAsync(IEnumerable<NativeSshStep> steps, int cols, int rows)
    {
        _writer.WriteHeader(cols, rows);

        foreach (NativeSshStep step in steps)
        {
            switch (step)
            {
                case EmitData data:
                    _writer.WriteDataEvent(data.Bytes);
                    break;
                case EmitResize resize:
                    _writer.WriteResizeEvent(resize.Cols, resize.Rows);
                    break;
                case Pause pause when pause.DelayMs > 0:
                    await Task.Delay(pause.DelayMs);
                    break;
            }
        }
    }
}

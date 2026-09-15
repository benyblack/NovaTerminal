using System.Collections.Generic;
using System.Text;

namespace NovaTerminal.ExternalSuites.NativeSsh;

public abstract record NativeSshStep;
public sealed record EmitData(byte[] Bytes) : NativeSshStep;
public sealed record EmitResize(int Cols, int Rows) : NativeSshStep;
public sealed record Pause(int DelayMs) : NativeSshStep;

public static class NativeSshScenarioPlan
{
    public static IEnumerable<NativeSshStep> GetScenario(string scenario) => scenario switch
    {
        "fullscreen-exit" => GetFullscreenExitScenario(),
        "prompt-return" => GetPromptReturnScenario(),
        "resize-burst" => GetResizeBurstScenario(),
        _ => throw new ArgumentException($"Unknown native SSH scenario: {scenario}")
    };

    private static IEnumerable<NativeSshStep> GetFullscreenExitScenario()
    {
        yield return Data("Connected to native.example\r\n");
        yield return Data("nova$ mc\r\n");
        yield return Delay(5);
        yield return Data("\u001b[?1049h\u001b[2J\u001b[H");
        yield return Data("Midnight Commander");
        yield return Delay(5);
        yield return Data("\u001b[?1049l");
        yield return Data("\r\nnova$ ");
    }

    private static IEnumerable<NativeSshStep> GetPromptReturnScenario()
    {
        yield return Data("nova$ echo hi\r\n");
        yield return Delay(5);
        yield return Data("hi\r\n");
        yield return Data("nova$ printf 'done'");
        yield return Delay(5);
        yield return Data("\r\ndone\r\n");
        yield return Data("nova$ ");
    }

    private static IEnumerable<NativeSshStep> GetResizeBurstScenario()
    {
        yield return Data("nova$ mc\r\n");
        yield return Data("\u001b[?1049h\u001b[2J\u001b[H");
        yield return Data("TUI 80x24");
        yield return Delay(5);
        yield return new EmitResize(100, 30);
        yield return Data("\u001b[H");
        yield return Data("TUI 100x30");
        yield return Delay(5);
        yield return new EmitResize(120, 30);
        yield return Data("\u001b[H");
        yield return Data("TUI 120x30");
        yield return Delay(5);
        yield return new EmitResize(90, 20);
        yield return Data("\u001b[H");
        yield return Data("TUI 90x20");
        yield return Delay(5);
        yield return Data("\u001b[?1049l");
        yield return Data("\r\nnova$ ");
    }

    private static EmitData Data(string text)
    {
        return new EmitData(Encoding.UTF8.GetBytes(text));
    }

    private static Pause Delay(int delayMs)
    {
        return new Pause(delayMs);
    }
}

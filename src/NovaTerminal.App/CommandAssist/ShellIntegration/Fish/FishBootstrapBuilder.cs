using System.IO;
using System.Text;

namespace NovaTerminal.CommandAssist.ShellIntegration.Fish;

public static class FishBootstrapBuilder
{
    public static string BuildScript()
    {
        const string nl = "\n";
        var b = new StringBuilder();
        b.Append("# Nova Terminal command-assist bootstrap for fish.").Append(nl);
        b.Append("# Installed as $XDG_CONFIG_HOME/fish/config.fish. We are the").Append(nl);
        b.Append("# user's config.fish for this session, so we explicitly source").Append(nl);
        b.Append("# the real ~/.config/fish/config.fish if it exists, then layer").Append(nl);
        b.Append("# our hooks on top so user prompt/fish_prompt stays user-owned.").Append(nl);
        b.Append(nl);
        b.Append("set -l __nova_user_config \"$HOME/.config/fish/config.fish\"").Append(nl);
        b.Append("if test -f \"$__nova_user_config\"").Append(nl);
        b.Append("    source \"$__nova_user_config\"").Append(nl);
        b.Append("end").Append(nl);
        b.Append(nl);
        b.Append("set -g __nova_command_start_ms \"\"").Append(nl);
        b.Append(nl);
        // Portable millisecond clock. `date +%s%N` is GNU-only; macOS/BSD
        // `date` leaves a literal "%N", which would break the `math` call.
        // Detect at runtime: if the output is digits-only, treat as
        // nanoseconds; otherwise fall back to second precision.
        b.Append("function __nova_now_ms").Append(nl);
        b.Append("    set -l raw (date +%s%N 2>/dev/null)").Append(nl);
        b.Append("    if string match -qr '^[0-9]+$' -- $raw").Append(nl);
        b.Append("        math \"$raw / 1000000\"").Append(nl);
        b.Append("    else").Append(nl);
        b.Append("        math (date +%s) \"* 1000\"").Append(nl);
        b.Append("    end").Append(nl);
        b.Append("end").Append(nl);
        b.Append(nl);
        b.Append("function __nova_emit_prompt_ready").Append(nl);
        b.Append("    printf '\\033]7;file://%s%s\\a' (hostname) (string escape --style=url -- $PWD)").Append(nl);
        b.Append("    printf '\\033]133;A\\a'").Append(nl);
        b.Append("end").Append(nl);
        b.Append(nl);
        // fish has native fish_preexec / fish_postexec events. We use
        // event handlers (function ... --on-event ...) so our hooks layer
        // cleanly without overwriting fish_prompt.
        b.Append("function __nova_preexec --on-event fish_preexec").Append(nl);
        b.Append("    set -l cmd \"$argv\"").Append(nl);
        b.Append("    set -l b64 (printf '%s' \"$cmd\" | base64 | tr -d '\\n')").Append(nl);
        b.Append("    printf '\\033]133;C;%s\\a' \"$b64\"").Append(nl);
        b.Append("    set -g __nova_command_start_ms (__nova_now_ms)").Append(nl);
        b.Append("end").Append(nl);
        b.Append(nl);
        b.Append("function __nova_postexec --on-event fish_postexec").Append(nl);
        b.Append("    set -l exit $status").Append(nl);
        b.Append("    if test -n \"$__nova_command_start_ms\"").Append(nl);
        b.Append("        set -l now_ms (__nova_now_ms)").Append(nl);
        b.Append("        set -l duration_ms (math $now_ms - $__nova_command_start_ms)").Append(nl);
        b.Append("        printf '\\033]133;D;%s;%s\\a' $exit $duration_ms").Append(nl);
        b.Append("        set -g __nova_command_start_ms \"\"").Append(nl);
        b.Append("    end").Append(nl);
        b.Append("end").Append(nl);
        b.Append(nl);
        // fish_prompt fires every prompt cycle. Hook (don't override) it
        // via a function with the same name preserved -- but since fish
        // only allows one function per name, we use a separate event hook
        // that fires after the prompt redraws.
        b.Append("function __nova_promptmark --on-event fish_prompt").Append(nl);
        b.Append("    __nova_emit_prompt_ready").Append(nl);
        b.Append("end").Append(nl);
        b.Append(nl);
        b.Append("__nova_emit_prompt_ready").Append(nl);
        return b.ToString();
    }

    public static string WriteScript(string targetDirectory)
    {
        // Fish reads $XDG_CONFIG_HOME/fish/config.fish on interactive startup.
        // Place our bootstrap in a fish/ subdirectory so XDG_CONFIG_HOME can
        // point at <root> while only fish-related files live in <root>/fish.
        string fishDir = Path.Combine(targetDirectory, "fish");
        Directory.CreateDirectory(fishDir);
        string path = Path.Combine(fishDir, "config.fish");
        File.WriteAllText(path, BuildScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}

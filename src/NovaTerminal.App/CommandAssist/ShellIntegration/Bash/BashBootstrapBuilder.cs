using System.IO;
using System.Text;

namespace NovaTerminal.CommandAssist.ShellIntegration.Bash;

public static class BashBootstrapBuilder
{
    public static string BuildScript()
    {
        const string nl = "\n";
        var b = new StringBuilder();
        b.Append("#!/usr/bin/env bash").Append(nl);
        b.Append("# Nova Terminal command-assist bootstrap. Sourced via `bash --rcfile`.").Append(nl);
        b.Append("# Source the user's bashrc first so customizations are preserved.").Append(nl);
        b.Append("if [ -f ~/.bashrc ]; then").Append(nl);
        b.Append("    . ~/.bashrc").Append(nl);
        b.Append("fi").Append(nl);
        b.Append(nl);
        b.Append("__nova_command_start_ms=\"\"").Append(nl);
        // Start armed-as-busy so the very first PROMPT_COMMAND cycle (which
        // runs before any user command is typed) cannot capture the user's
        // own PROMPT_COMMAND helpers as a phantom "accepted command".
        // __nova_arm clears this back to 0 at the end of each prompt cycle,
        // immediately before bash returns to readline for the next input.
        b.Append("__nova_command_active=1").Append(nl);
        b.Append(nl);
        // Portable millisecond clock. `date +%s%N` is GNU-only; on
        // macOS/BSD `date` it leaves a literal "%N" that breaks
        // arithmetic. Prefer the bash 5+ built-in $EPOCHREALTIME
        // (microsecond precision, no external process), fall back to
        // `date +%s` for older bash.
        b.Append("__nova_now_ms() {").Append(nl);
        b.Append("    if [ -n \"${EPOCHREALTIME:-}\" ]; then").Append(nl);
        b.Append("        local sec=\"${EPOCHREALTIME%.*}\"").Append(nl);
        b.Append("        local frac=\"${EPOCHREALTIME#*.}\"").Append(nl);
        b.Append("        printf '%s%s' \"$sec\" \"${frac:0:3}\"").Append(nl);
        b.Append("    else").Append(nl);
        b.Append("        printf '%s000' \"$(date +%s)\"").Append(nl);
        b.Append("    fi").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        b.Append("__nova_url_encode_pwd() {").Append(nl);
        b.Append("    local s=\"$PWD\"").Append(nl);
        b.Append("    printf '%s' \"$s\" | LC_ALL=C awk 'BEGIN{for(i=0;i<256;i++)c[sprintf(\"%c\",i)]=i} {for(i=1;i<=length($0);i++){ch=substr($0,i,1); if(ch~/[A-Za-z0-9._~\\/-]/) printf \"%s\",ch; else printf \"%%%02X\",c[ch]}}'").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        b.Append("__nova_emit_prompt_ready() {").Append(nl);
        b.Append("    printf '\\033]7;file://%s%s\\a' \"${HOSTNAME:-localhost}\" \"$(__nova_url_encode_pwd)\"").Append(nl);
        b.Append("    printf '\\033]133;A\\a'").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        b.Append("__nova_emit_completion() {").Append(nl);
        b.Append("    local exit=$1").Append(nl);
        b.Append("    if [ -z \"$__nova_command_start_ms\" ]; then").Append(nl);
        b.Append("        return").Append(nl);
        b.Append("    fi").Append(nl);
        b.Append("    local now_ms duration_ms").Append(nl);
        b.Append("    now_ms=$(__nova_now_ms)").Append(nl);
        b.Append("    duration_ms=$((now_ms - __nova_command_start_ms))").Append(nl);
        b.Append("    printf '\\033]133;D;%s;%s\\a' \"$exit\" \"$duration_ms\"").Append(nl);
        b.Append("    __nova_command_start_ms=\"\"").Append(nl);
        // NOTE: deliberately do NOT clear __nova_command_active here.
        // The user's PROMPT_COMMAND statements run AFTER this function in
        // the same prompt cycle; if we cleared active here, each of those
        // statements would fire the DEBUG trap with active=0 and be
        // captured as the user's "accepted command". __nova_arm (which
        // runs LAST in PROMPT_COMMAND) is responsible for clearing it.
        b.Append("}").Append(nl);
        b.Append(nl);
        b.Append("__nova_arm() {").Append(nl);
        b.Append("    __nova_command_active=0").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        // Bash has no native preexec. We approximate it via the DEBUG trap,
        // which fires before every simple command -- including from inside
        // PROMPT_COMMAND. To capture only the user-entered command line, we
        // arm a one-shot flag in PROMPT_COMMAND and disarm it on the first
        // DEBUG fire that follows.
        b.Append("__nova_preexec() {").Append(nl);
        b.Append("    if [ \"$__nova_command_active\" = \"1\" ]; then").Append(nl);
        b.Append("        return").Append(nl);
        b.Append("    fi").Append(nl);
        b.Append("    local cmd=\"$BASH_COMMAND\"").Append(nl);
        b.Append("    case \"$cmd\" in").Append(nl);
        b.Append("        __nova_*|trap*|PROMPT_COMMAND*) return ;;").Append(nl);
        b.Append("    esac").Append(nl);
        b.Append("    local b64").Append(nl);
        b.Append("    b64=$(printf '%s' \"$cmd\" | base64 | tr -d '\\n')").Append(nl);
        b.Append("    printf '\\033]133;C;%s\\a' \"$b64\"").Append(nl);
        b.Append("    __nova_command_start_ms=$(__nova_now_ms)").Append(nl);
        b.Append("    __nova_command_active=1").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        b.Append("__nova_precmd() {").Append(nl);
        b.Append("    local exit=$?").Append(nl);
        b.Append("    __nova_emit_completion \"$exit\"").Append(nl);
        b.Append("    __nova_emit_prompt_ready").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        b.Append("trap '__nova_preexec' DEBUG").Append(nl);
        // Bracket the user's PROMPT_COMMAND with __nova_precmd (first: emit
        // the previous command's D + new prompt-ready A) and __nova_arm
        // (last: release the DEBUG-trap suppression so the user's next typed
        // command is the first one captured). The DEBUG fires inside this
        // chain stay suppressed because __nova_command_active is still 1
        // throughout -- user PROMPT_COMMAND helpers cannot masquerade as
        // accepted commands.
        b.Append("if [ -n \"$PROMPT_COMMAND\" ]; then").Append(nl);
        b.Append("    PROMPT_COMMAND=\"__nova_precmd; $PROMPT_COMMAND; __nova_arm\"").Append(nl);
        b.Append("else").Append(nl);
        b.Append("    PROMPT_COMMAND='__nova_precmd; __nova_arm'").Append(nl);
        b.Append("fi").Append(nl);
        b.Append(nl);
        b.Append("__nova_emit_prompt_ready").Append(nl);
        return b.ToString();
    }

    public static string WriteScript(string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        string path = Path.Combine(targetDirectory, "command-assist-bootstrap.bash");
        File.WriteAllText(path, BuildScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}

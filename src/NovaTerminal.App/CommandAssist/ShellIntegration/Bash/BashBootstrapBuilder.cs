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
        b.Append("__nova_command_active=0").Append(nl);
        b.Append(nl);
        b.Append("__nova_now_ms() {").Append(nl);
        b.Append("    printf '%s' \"$(($(date +%s%N)/1000000))\"").Append(nl);
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
        b.Append("if [ -n \"$PROMPT_COMMAND\" ]; then").Append(nl);
        b.Append("    PROMPT_COMMAND=\"__nova_precmd; $PROMPT_COMMAND\"").Append(nl);
        b.Append("else").Append(nl);
        b.Append("    PROMPT_COMMAND='__nova_precmd'").Append(nl);
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

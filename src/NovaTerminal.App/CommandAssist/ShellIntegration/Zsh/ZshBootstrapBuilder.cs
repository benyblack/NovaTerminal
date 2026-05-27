using System.IO;
using System.Text;

namespace NovaTerminal.CommandAssist.ShellIntegration.Zsh;

public static class ZshBootstrapBuilder
{
    public static string BuildScript()
    {
        const string nl = "\n";
        var b = new StringBuilder();
        b.Append("#!/usr/bin/env zsh").Append(nl);
        b.Append("# Nova Terminal command-assist bootstrap for zsh.").Append(nl);
        b.Append("# Installed as $ZDOTDIR/.zshrc. ZDOTDIR is set so the user's").Append(nl);
        b.Append("# ~/.zshrc is NOT auto-sourced; we source it explicitly first").Append(nl);
        b.Append("# so customizations and PROMPT/PS1 stay owned by the user.").Append(nl);
        b.Append("if [ -f \"$HOME/.zshrc\" ]; then").Append(nl);
        b.Append("    . \"$HOME/.zshrc\"").Append(nl);
        b.Append("fi").Append(nl);
        b.Append(nl);
        b.Append("typeset -g __nova_command_start_ms=\"\"").Append(nl);
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
        b.Append("    printf '\\033]7;file://%s%s\\a' \"${HOST:-localhost}\" \"$(__nova_url_encode_pwd)\"").Append(nl);
        b.Append("    printf '\\033]133;A\\a'").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        // zsh has native preexec/precmd hooks via the function-array convention.
        // preexec receives the about-to-run command as $1, so unlike bash we
        // do not need a one-shot guard around the DEBUG trap.
        b.Append("__nova_preexec() {").Append(nl);
        b.Append("    local cmd=\"$1\"").Append(nl);
        b.Append("    local b64").Append(nl);
        b.Append("    b64=$(printf '%s' \"$cmd\" | base64 | tr -d '\\n')").Append(nl);
        b.Append("    printf '\\033]133;C;%s\\a' \"$b64\"").Append(nl);
        b.Append("    __nova_command_start_ms=$(__nova_now_ms)").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        b.Append("__nova_precmd() {").Append(nl);
        b.Append("    local exit=$?").Append(nl);
        b.Append("    if [ -n \"$__nova_command_start_ms\" ]; then").Append(nl);
        b.Append("        local now_ms duration_ms").Append(nl);
        b.Append("        now_ms=$(__nova_now_ms)").Append(nl);
        b.Append("        duration_ms=$((now_ms - __nova_command_start_ms))").Append(nl);
        b.Append("        printf '\\033]133;D;%s;%s\\a' \"$exit\" \"$duration_ms\"").Append(nl);
        b.Append("        __nova_command_start_ms=\"\"").Append(nl);
        b.Append("    fi").Append(nl);
        b.Append("    __nova_emit_prompt_ready").Append(nl);
        b.Append("}").Append(nl);
        b.Append(nl);
        b.Append("typeset -ag precmd_functions preexec_functions").Append(nl);
        b.Append("precmd_functions+=(__nova_precmd)").Append(nl);
        b.Append("preexec_functions+=(__nova_preexec)").Append(nl);
        b.Append(nl);
        b.Append("__nova_emit_prompt_ready").Append(nl);
        return b.ToString();
    }

    public static string WriteScript(string targetDirectory)
    {
        // ZDOTDIR must point at a directory that only contains zsh startup
        // files; live next to other shells' bootstrap files would let zsh
        // mistakenly source .zshenv-like neighbors. Carve out a zsh-only
        // subdirectory and write .zshrc into it.
        string zshDir = Path.Combine(targetDirectory, "zsh");
        Directory.CreateDirectory(zshDir);
        string path = Path.Combine(zshDir, ".zshrc");
        File.WriteAllText(path, BuildScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}

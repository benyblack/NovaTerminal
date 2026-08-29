#!/usr/bin/env bash
printf '\033[38;5;213m  ███╗   ██╗ ██████╗ ██╗   ██╗ █████╗ \033[0m\n'
printf '\033[38;5;213m  ████╗  ██║██╔═══██╗██║   ██║██╔══██╗\033[0m\n'
printf '\033[38;5;213m  ██╔██╗ ██║██║   ██║██║   ██║███████║\033[0m\n'
printf '\033[38;5;177m  ██║╚██╗██║██║   ██║╚██╗ ██╔╝██╔══██║\033[0m\n'
printf '\033[38;5;177m  ██║ ╚████║╚██████╔╝ ╚████╔╝ ██║  ██║\033[0m\n'
printf '\n'
# Version mirrors Directory.Build.props's <Version> (currently 0.5.0). This is a static string,
# not read from the repo at capture time, so re-check it against Directory.Build.props whenever
# this banner is re-published - it will drift silently otherwise.
printf '  \033[1mterminal\033[0m   NovaTerminal 0.5.0 (win-x64)\n'
# "partial" is the honest summary of src/NovaTerminal.App/Resources/vt-conformance-report.json's
# own numbers (18 of 58 rows fully supported, 34 partial) - re-check that file if this banner is
# ever revisited, rather than restoring a flat percentage that overclaims.
printf '  \033[1mengine\033[0m     VT parser · partial ANSI/xterm conformance\n'
printf '  \033[1mrenderer\033[0m   Skia · GPU glyph cache\n'
printf '  \033[1mbackend\033[0m    Rust PTY\n'
# Matches DemoWorld.SeedSettings's seeded baseline (observe on, act off) so this banner never
# contradicts the settings screenshot published alongside it.
printf '  \033[1magents\033[0m     MCP observe \033[32m●\033[0m  act \033[90m○\033[0m\n'
printf '\n'

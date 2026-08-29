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
# Derived from the actual seeded settings, not restated: DemoWorld.SeedSettings exports what it
# just wrote (after a scenario's own `customize` override runs) as NOVA_SHOTS_AGENT_OBSERVE_ON /
# NOVA_SHOTS_AGENT_ACT_ON, and this reads them back rather than hardcoding "observe on, act off".
# A scenario like AgentSessionScenario or ClipAgentScenario that turns act on to demonstrate it
# therefore gets a banner whose dots agree with what it does next in the same asset, and a future
# scenario that changes either toggle cannot silently make this banner lie again. Defaults below
# (observe on, act off) match SeedSettings's own baseline and only apply if the variable is unset
# - e.g. this script run by hand outside the harness - never as a fallback for a scenario that
# actually set it.
observe_dot=$'\033[90m○\033[0m'
if [ "${NOVA_SHOTS_AGENT_OBSERVE_ON:-1}" = "1" ]; then
  observe_dot=$'\033[32m●\033[0m'
fi

act_dot=$'\033[90m○\033[0m'
if [ "${NOVA_SHOTS_AGENT_ACT_ON:-0}" = "1" ]; then
  act_dot=$'\033[32m●\033[0m'
fi

printf '  \033[1magents\033[0m     MCP observe %s  act %s\n' "$observe_dot" "$act_dot"
printf '\n'

#!/usr/bin/env bash
# A fabricated `top`-style snapshot, not the real thing. `ps aux` (what this replaced) prints
# every byte from the real OS process table - the capture machine's real uid/gid, its real TTY
# names, real PIDs, and this very harness's own bash/grep/head processes - which DemoWorld's PS1
# and environment overrides cannot touch, because none of that text passes through them. This
# script is plain printf: nothing it prints came from the real machine, so there is nothing here
# for the demo environment to fail to mask. Row count is fixed and small on purpose, so the table
# never needs to scroll to be seen whole in a quarter-height pane.
printf '\033[90mtop - demo world uptime 4 days, 9 processes, load 0.42 0.38 0.31\033[0m\n'
printf '\033[7m%-6s %-10s %5s %5s %-9s %-8s %s\033[0m\n' "PID" "USER" "%CPU" "%MEM" "STATE" "TIME" "COMMAND"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1042" "nova" "12.4" " 3.1" "running" "00:02:11" "novaterm-app"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1058" "nova" " 6.7" " 1.8" "running" "00:01:47" "vt-parser"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1061" "nova" " 4.2" " 2.4" "sleeping" "00:00:52" "skia-render"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1073" "nova" " 3.9" " 1.2" "sleeping" "00:00:38" "pty-bridge"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1088" "nova" " 2.1" " 0.9" "sleeping" "00:00:21" "agent-host"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1096" "nova" " 1.6" " 0.8" "sleeping" "00:00:15" "sixel-decode"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1104" "nova" " 0.8" " 0.6" "sleeping" "00:00:09" "journald"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1119" "nova" " 0.3" " 0.4" "sleeping" "00:00:04" "watchdog"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1140" "nova" " 0.1" " 0.2" "sleeping" "00:00:01" "bash"

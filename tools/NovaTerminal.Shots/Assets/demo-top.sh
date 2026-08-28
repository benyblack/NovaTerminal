#!/usr/bin/env bash
# A fabricated `top`-style snapshot, not the real thing. `ps aux` (what this replaced) prints
# every byte from the real OS process table - the capture machine's real uid/gid, its real TTY
# names, real PIDs, and this very harness's own bash/grep/head processes - which DemoWorld's PS1
# and environment overrides cannot touch, because none of that text passes through them. This
# script is plain printf: nothing it prints came from the real machine, so there is nothing here
# for the demo environment to fail to mask.
#
# Row count (22 lines: 2 header + 20 process rows) is fixed and chosen to fill hero-split's
# bottom-right pane close to its bottom edge without overflowing it. Tuned against the actual
# captured image, not the raw PTY row count: the pane's own row count reported at spawn time
# (~26-27) left no room for the command's own echoed line once a 22-process table (24 content
# rows) was tried - the echo scrolled off the top, the exact failure this task calls worse than a
# small gap. 20 process rows leaves a one-row margin. See HeroSplitScenario's remarks for the
# full row budget this was tuned against. Extended from an original 9-row table (round 1) because
# 9 rows left the pane roughly half blank, which is what this task's Job 1 fix addresses.
printf '\033[90mtop - demo world uptime 4 days, 20 processes, load 0.42 0.38 0.31\033[0m\n'
printf '\033[7m%-6s %-10s %5s %5s %-9s %-8s %s\033[0m\n' "PID" "USER" "%CPU" "%MEM" "STATE" "TIME" "COMMAND"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1042" "nova" "12.4" " 3.1" "running" "00:02:11" "novaterm-app"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1058" "nova" " 6.7" " 1.8" "running" "00:01:47" "vt-parser"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1061" "nova" " 4.2" " 2.4" "sleeping" "00:00:52" "skia-render"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1073" "nova" " 3.9" " 1.2" "sleeping" "00:00:38" "pty-bridge"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1176" "nova" " 3.5" " 2.0" "running" "00:00:34" "render-cache"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1183" "nova" " 3.1" " 1.7" "sleeping" "00:00:30" "journal-writer"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1088" "nova" " 2.1" " 0.9" "sleeping" "00:00:21" "agent-host"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1194" "nova" " 1.9" " 1.1" "sleeping" "00:00:19" "metrics-agg"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1096" "nova" " 1.6" " 0.8" "sleeping" "00:00:15" "sixel-decode"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1202" "nova" " 1.4" " 0.9" "sleeping" "00:00:13" "palette-cache"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1211" "nova" " 1.2" " 0.7" "running" "00:00:11" "pty-writer"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1219" "nova" " 1.0" " 0.6" "sleeping" "00:00:09" "session-sync"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1226" "nova" " 0.9" " 0.5" "sleeping" "00:00:08" "font-shaper"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1233" "nova" " 0.8" " 0.5" "sleeping" "00:00:07" "theme-loader"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1104" "nova" " 0.8" " 0.6" "sleeping" "00:00:09" "journald"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1240" "nova" " 0.6" " 0.4" "sleeping" "00:00:06" "update-check"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1247" "nova" " 0.5" " 0.4" "sleeping" "00:00:05" "clip-store"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1254" "nova" " 0.4" " 0.3" "sleeping" "00:00:04" "net-bridge"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1119" "nova" " 0.3" " 0.4" "sleeping" "00:00:04" "watchdog"
printf '%-6s %-10s %5s %5s %-9s %-8s %s\n' "1140" "nova" " 0.1" " 0.2" "sleeping" "00:00:01" "bash"

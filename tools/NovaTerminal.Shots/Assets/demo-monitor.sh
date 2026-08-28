#!/usr/bin/env bash
# A fabricated htop-style process monitor, standing in because htop, btop and top are all
# missing on the capture machine (checked directly, not assumed). It enters the alternate
# screen, draws a full-screen process table, refreshes it a couple of times so this is a real
# redrawing TUI and not a single static blit, then parks waiting for 'q' - the same
# alternate-screen life cycle a real curses monitor uses, exercised through the real VT parser.
#
# Nothing below is machine-derived: no $(...), no backticks, no $RANDOM, no real PIDs or
# usernames. Every row is a literal printf, always user "nova" - demo-top.sh's established
# pattern, extended here to a full-screen alternate-screen view instead of an inline table.
#
# Per-frame pacing is opt-in via NOVA_SHOTS_PACE, not always-on, for the same reason
# demo-test.sh's is: a still capture only ever needs the finished (third) frame, so a fixed
# real sleep between redraws would only be a way for a loaded machine to blow this command's
# settle budget for no benefit to the still. Task 16's clip-tui, which wants the refreshes to
# actually animate on camera, is the intended consumer of the paced path.
pace() {
  if [ -n "${NOVA_SHOTS_PACE:-}" ]; then
    sleep 0.3
  fi
}

draw_frame() {
  printf '\033[H\033[2J'
  printf '\033[7m demo-monitor - 4 cores  load average: 0.42 0.38 0.31  uptime 4 days \033[0m\n'
  printf '\033[36m1\033[0m[\033[32m|||||||||\033[0m.....]  \033[36m2\033[0m[\033[32m||||||\033[0m........]\n'
  printf '\033[36m3\033[0m[\033[32m|||||||||||\033[0m...]  \033[36m4\033[0m[\033[32m|||\033[0m...........]\n'
  printf 'Mem[\033[33m||||||||||||\033[0m..................] 3.1G/8.0G\n'
  printf 'Swp[\033[0m....................................] 0K/2.0G\n\n'
  printf '\033[7m%6s %-8s %5s %5s %-9s %-8s %s\033[0m\n' "PID" "USER" "CPU%" "MEM%" "STATE" "TIME" "COMMAND"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1042" "nova" "12.4" " 3.1" "running" "00:02:11" "novaterm-app"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1058" "nova" " 9.8" " 1.8" "running" "00:01:47" "vt-parser"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1061" "nova" " 6.2" " 2.4" "running" "00:00:52" "skia-render"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1073" "nova" " 4.9" " 1.2" "sleeping" "00:00:38" "pty-bridge"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1176" "nova" " 4.5" " 2.0" "running" "00:00:34" "render-cache"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1183" "nova" " 3.8" " 1.7" "sleeping" "00:00:30" "journal-writer"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1088" "nova" " 3.1" " 0.9" "sleeping" "00:00:21" "agent-host"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1194" "nova" " 2.6" " 1.1" "sleeping" "00:00:19" "metrics-agg"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1096" "nova" " 2.2" " 0.8" "sleeping" "00:00:15" "sixel-decode"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1202" "nova" " 1.9" " 0.9" "sleeping" "00:00:13" "palette-cache"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1211" "nova" " 1.7" " 0.7" "running" "00:00:11" "pty-writer"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1219" "nova" " 1.5" " 0.6" "sleeping" "00:00:09" "session-sync"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1226" "nova" " 1.3" " 0.5" "sleeping" "00:00:08" "font-shaper"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1233" "nova" " 1.1" " 0.5" "sleeping" "00:00:07" "theme-loader"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1104" "nova" " 1.0" " 0.6" "sleeping" "00:00:09" "journald"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1240" "nova" " 0.9" " 0.4" "sleeping" "00:00:06" "update-check"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1247" "nova" " 0.8" " 0.4" "sleeping" "00:00:05" "clip-store"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1254" "nova" " 0.7" " 0.3" "sleeping" "00:00:04" "net-bridge"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1119" "nova" " 0.6" " 0.4" "sleeping" "00:00:04" "watchdog"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1261" "nova" " 0.5" " 0.3" "sleeping" "00:00:03" "clip-encoder"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1268" "nova" " 0.4" " 0.3" "sleeping" "00:00:03" "glyph-cache"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1275" "nova" " 0.3" " 0.2" "sleeping" "00:00:02" "search-index"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1282" "nova" " 0.2" " 0.2" "sleeping" "00:00:01" "scroll-cache"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1289" "nova" " 0.2" " 0.2" "sleeping" "00:00:01" "codec-mux"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1296" "nova" " 0.1" " 0.1" "sleeping" "00:00:01" "shader-warm"
  printf '%6s %-8s %5s %5s %-9s %-8s %s\n' "1140" "nova" " 0.1" " 0.2" "sleeping" "00:00:01" "bash"
  printf '\n\033[2mF1Help  F2Setup  F3Search  F5Tree  F6SortBy  F9Kill  F10Quit\033[0m\n'
}

printf '\033[?1049h'

draw_frame
pace
draw_frame
pace
draw_frame

while :; do
  IFS= read -r -n 1 key
  if [ "$key" = "q" ]; then
    break
  fi
done

printf '\033[?1049l'

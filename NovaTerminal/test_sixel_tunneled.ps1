$esc = [char]27
# Larger Sixel: 100x36 pixels, red
$sixelData = "q#1;2;100;0;0#1" + ("!100~-" * 6)

Write-Host "--- Sixel Graphics Test (OSC 1339) ---"
Write-Host -NoNewline "$($esc)[1;1H" # Reset cursor to top-left
Write-Host -NoNewline "$($esc)[1G$($esc)]1339;$sixelData$($esc)\"
Write-Host "`nDone."

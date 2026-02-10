$esc = [char]27
$bel = [char]7
# 1x1 red pixel PNG
$base64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQAgALCQCE9I638wAAAABJRU5ErkJggg=="
Write-Host -NoNewline "$($esc)]1337;File=inline=1;width=5;height=2:$base64$($bel)"
Write-Host ""

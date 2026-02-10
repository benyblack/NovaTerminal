$esc = [char]27
$bel = [char]7
# 32x32 red PNG (Known good)
$base64 = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAALUlEQVR42u3BqREAAAgEwZ9391vAChf6NoXUAAAAAAAAAAAAAAAAAAAAAAAAAOA2B89AAAFsT204AAAAAElFTkSuQmCC"

Write-Host "--- Kitty Graphics Test (OSC 1339) ---"
Write-Host -NoNewline "$($esc)[1;1H" # Reset cursor to top-left

Write-Host "Single chunk (Should be a RED square):"
Write-Host -NoNewline "$($esc)[1G$($esc)]1339;K:Ga=T,f=100,m=0,c=20,r=10;$base64$($bel)"
Write-Host "`n"

Write-Host "Multi-chunk (32x32 RED, Multi-chunk):"
$chunk1 = $base64.Substring(0, 50)
$chunk2 = $base64.Substring(50)

Write-Host -NoNewline "$($esc)[1G$($esc)]1339;K:Ga=T,f=100,m=1,c=20,r=10;$chunk1$($bel)"
Write-Host -NoNewline "$($esc)]1339;K:m=0;$chunk2$($bel)"
Write-Host "`nDone."

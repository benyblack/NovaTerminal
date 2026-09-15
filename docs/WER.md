Run this in PowerShell to remove the WER capture for Ntilde:

```powershell
Remove-Item -Path "HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\Ntilde.exe" -Recurse -Force -ErrorAction SilentlyContinue
```


That deletes only the Ntilde.exe capture rule; it leaves any global WER settings and other apps' rules untouched. Takes effect immediately — no restart needed.

To re-enable it later (full crash dumps, last 5 kept):

```powershell
$k = "HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\Ntilde.exe"
New-Item -Path $k -Force | Out-Null
Set-ItemProperty -Path $k -Name DumpFolder -Value "$env:LOCALAPPDATA\CrashDumps" -Type ExpandString
Set-ItemProperty -Path $k -Name DumpType   -Value 2 -Type DWord
Set-ItemProperty -Path $k -Name DumpCount   -Value 5 -Type DWord
```

To check whether it's currently armed:

```powershell
Get-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\Ntilde.exe" -ErrorAction SilentlyContinue
```

(No output = disabled.) The captured .dmp files in %LOCALAPPDATA%\CrashDumps aren't removed by disabling — delete them manually if you want the space back; each is ~20 MB.
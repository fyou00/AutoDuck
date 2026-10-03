# Menjalankan AutoDuck.Helper otomatis saat login Windows (shortcut di folder Startup, tanpa admin).
param([string]$ExePath = (Join-Path $PSScriptRoot "..\helper\publish\AutoDuck.Helper.exe"))

$ExePath = (Resolve-Path $ExePath).Path
$lnk = Join-Path ([Environment]::GetFolderPath("Startup")) "AutoDuck Helper.lnk"
$ws = New-Object -ComObject WScript.Shell
$s = $ws.CreateShortcut($lnk)
$s.TargetPath = $ExePath
$s.Arguments = "--background"
$s.WorkingDirectory = Split-Path $ExePath
$s.WindowStyle = 7   # minimized
$s.Save()
Write-Host "Startup shortcut dibuat: $lnk"

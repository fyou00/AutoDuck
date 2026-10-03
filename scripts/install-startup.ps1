# Start AutoDuck.Helper automatically when you log in to Windows (Startup-folder shortcut, no admin needed).
param([string]$ExePath)

if (-not $ExePath) {
    $candidates = @(
        (Join-Path $PSScriptRoot "AutoDuck.Helper.exe"),
        (Join-Path $PSScriptRoot "..\helper\publish\AutoDuck.Helper.exe")
    )
    $ExePath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $ExePath) { Write-Error "AutoDuck.Helper.exe not found. Pass it with -ExePath."; exit 1 }

$ExePath = (Resolve-Path $ExePath).Path
$lnk = Join-Path ([Environment]::GetFolderPath("Startup")) "AutoDuck Helper.lnk"
$s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$s.TargetPath = $ExePath
$s.Arguments = "--background"
$s.WorkingDirectory = Split-Path $ExePath
$s.WindowStyle = 7   # minimized
$s.Save()
Write-Host "Startup shortcut created: $lnk"

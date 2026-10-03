$lnk = Join-Path ([Environment]::GetFolderPath("Startup")) "AutoDuck Helper.lnk"
if (Test-Path $lnk) { Remove-Item $lnk; Write-Host "Startup shortcut removed." } else { Write-Host "No startup shortcut found." }

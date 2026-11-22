# Hallow Home installer: copies the app to your user folder (no admin needed) and adds Start menu + Desktop shortcuts.
# It changes nothing else. Home Assistant is NOT installed here - it runs on your NAS (see NAS-SETUP.md).
#   Install.ps1 [-NoStart]   (-NoStart: don't open the app afterwards)
param([switch]$NoStart)
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Hallow Home'
New-Item -ItemType Directory -Force $dest | Out-Null
$wasRunning = [bool](Get-Process HallowHome -ErrorAction SilentlyContinue)
if ($wasRunning) { try { $old = Join-Path $dest "HallowHome.exe"; if (Test-Path $old) { & $old --cmd quit | Out-Null; Start-Sleep -Milliseconds 1500 } } catch { } }
Get-Process HallowHome -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
foreach ($f in 'HallowHome.exe', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll', 'WebView2Loader.dll', 'README.txt', 'NAS-SETUP.md', 'Uninstall.ps1', 'FontAwesome-LICENSE.txt', 'WebView2-LICENSE.txt', 'WebView2-NOTICE.txt') {
    Copy-Item (Join-Path $PSScriptRoot $f) $dest -Force
}
$exe = Join-Path $dest 'HallowHome.exe'
# Windows Settings > Apps entry (per user, no admin)
$u = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HallowHome'
New-Item $u -Force | Out-Null
$ver = (Get-Item $exe).VersionInfo.FileVersion -replace '\.0\.0$', ''
$vals = @{ DisplayName = 'Hallow Home'; DisplayVersion = $ver; Publisher = 'Hallow'; DisplayIcon = $exe; InstallLocation = $dest
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $dest 'Uninstall.ps1')`""
    URLInfoAbout = 'https://github.com/Dom428/hallow-home' }
foreach ($k in $vals.Keys) { Set-ItemProperty $u -Name $k -Value $vals[$k] }
Set-ItemProperty $u -Name NoModify -Value 1 -Type DWord; Set-ItemProperty $u -Name NoRepair -Value 1 -Type DWord
Set-ItemProperty $u -Name EstimatedSize -Value ([int]((Get-ChildItem $dest | Measure-Object Length -Sum).Sum / 1KB)) -Type DWord
$w = New-Object -ComObject WScript.Shell
foreach ($dir in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) {
    $s = $w.CreateShortcut((Join-Path $dir 'Hallow Home.lnk'))
    $s.TargetPath = $exe; $s.WorkingDirectory = $dest; $s.Description = 'Your pets'' feeder and fountain, cameras and lock through Home Assistant'; $s.Save()
}
if ($NoStart) { Write-Host "Hallow Home is installed. It's in your Start menu and on your Desktop."; return }
# after an update it comes back the way it was (in the tray if it was running); a fresh install opens the window
if ($wasRunning) { Start-Process $exe -ArgumentList '--tray' } else { Start-Process $exe }
Write-Host "Hallow Home is installed. It's in your Start menu and on your Desktop."

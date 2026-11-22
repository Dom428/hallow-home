# Hallow Home uninstaller (run from Windows Settings > Apps, or directly). Home Assistant on your NAS isn't touched.
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Hallow Home'
$exe = Join-Path $dest 'HallowHome.exe'
Add-Type -AssemblyName System.Windows.Forms
if ([System.Windows.Forms.MessageBox]::Show("Uninstall Hallow Home?`n`nIts settings, the saved Home Assistant token and alert addresses are removed from this PC. Home Assistant on your NAS stays as it is (revoke the token there if you like).", 'Uninstall Hallow Home', 'YesNo', 'Question') -ne 'Yes') { return }
if (Get-Process HallowHome -ErrorAction SilentlyContinue) { try { & $exe --cmd quit | Out-Null; Start-Sleep -Milliseconds 1500 } catch { } }
Get-Process HallowHome -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'HallowHome' -ErrorAction SilentlyContinue
foreach ($dir in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) { Remove-Item (Join-Path $dir 'Hallow Home.lnk') -ErrorAction SilentlyContinue }
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HallowHome' -Recurse -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA 'HallowHome') -Recurse -Force -ErrorAction SilentlyContinue        # settings, history, the encrypted token / webhook / topic
Remove-Item (Join-Path $env:LOCALAPPDATA 'HallowHome') -Recurse -Force -ErrorAction SilentlyContinue   # the live view's WebView2 cache
Start-Process cmd.exe -ArgumentList "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$dest`"" -WindowStyle Hidden
[System.Windows.Forms.MessageBox]::Show("Hallow Home is uninstalled.`n`nSnapshots you saved stay in Pictures\Hallow Home.", 'Hallow Home') | Out-Null

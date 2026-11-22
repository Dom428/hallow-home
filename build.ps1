# Builds HallowHome.exe with the C# compiler that ships with Windows (.NET Framework 4.8) - nothing to install.
# The only outside parts are Microsoft's WebView2 SDK DLLs in lib\ (NuGet Microsoft.Web.WebView2, for the optional camera live view).
#   .\build.ps1          the app (x64)
#   .\build.ps1 -Tests   also tests\HomeTests.exe (unit tests + a fake Home Assistant: REST, WebSocket state_changed, service calls,
#                        an MJPEG camera made of generated frames; and the family pipe of a test copy talking to it)
param([switch]$Tests)
$ErrorActionPreference = 'Stop'
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
$src = Join-Path $PSScriptRoot 'src'
$lib = Join-Path $PSScriptRoot 'lib'
$out = Join-Path $PSScriptRoot 'HallowHome.exe'
$refs = "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll", "$fw\System.Xaml.dll",
    "$fw\System.Windows.Forms.dll", "$fw\System.Drawing.dll", "$fw\System.Runtime.Serialization.dll", "$fw\System.Core.dll",
    "$fw\System.IO.Compression.dll", "$fw\System.IO.Compression.FileSystem.dll", "$fw\System.Web.Extensions.dll", "$fw\System.Xml.dll", "$fw\System.Security.dll",
    "$lib\Microsoft.Web.WebView2.Core.dll", "$lib\Microsoft.Web.WebView2.Wpf.dll"
$common = @('/nologo', '/optimize+', '/platform:x64', "/win32icon:$src\hallow.ico", "/resource:$src\Theme.xaml,HallowHome.Theme.xaml") + ($refs | ForEach-Object { "/reference:$_" })
$files = Get-ChildItem $src -Filter *.cs | ForEach-Object FullName
& "$fw\csc.exe" @($common + @('/target:winexe', "/out:$out") + $files)
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
foreach ($d in 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll', 'WebView2Loader.dll') { Copy-Item "$lib\$d" $PSScriptRoot -Force }
"Built $out ($([math]::Round((Get-Item $out).Length / 1KB)) KB)"
if ($Tests) {
    $tdir = Join-Path $PSScriptRoot 'tests'
    $t = Join-Path $tdir 'HomeTests.exe'
    & "$fw\csc.exe" @($common + @('/target:exe', "/out:$t", '/main:HallowHome.Tests.Run') + $files + (Get-ChildItem $tdir -Filter *.cs | ForEach-Object FullName))
    if ($LASTEXITCODE -ne 0) { throw "Test build failed" }
    foreach ($d in 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll', 'WebView2Loader.dll') { Copy-Item "$lib\$d" $tdir -Force }
    "Built $t"
}

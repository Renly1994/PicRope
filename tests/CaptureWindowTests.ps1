$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$build = Join-Path $projectRoot 'build'
& (Join-Path $projectRoot 'packaging\prepare-assets.ps1')
$references = @(
    "/r:$framework\WPF\PresentationCore.dll", "/r:$framework\WPF\PresentationFramework.dll", "/r:$framework\WPF\WindowsBase.dll",
    "/r:$framework\System.Xaml.dll", "/r:$framework\System.Web.Extensions.dll", "/r:$framework\Microsoft.VisualBasic.dll",
    "/r:$projectRoot\vendor\WebView2\Microsoft.Web.WebView2.Core.dll", "/r:$projectRoot\vendor\WebView2\Microsoft.Web.WebView2.Wpf.dll",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Net.Http.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll'
)
$resources = @(Get-ChildItem -LiteralPath (Join-Path $build 'runtime-assets') -Filter 'basket-*.png' | ForEach-Object { '/resource:' + $_.FullName + ',PicRope.Basket.' + $_.BaseName.Substring(7) })
$resources += @(Get-ChildItem -LiteralPath (Join-Path $build 'runtime-assets') -Filter 'envelope-*.png' | ForEach-Object { '/resource:' + $_.FullName + ',PicRope.Envelope.' + $_.BaseName.Substring(9) })
$sources = (Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs').FullName
$icon = Join-Path $projectRoot 'src\assets\PicRope.ico'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /codepage:65001 /main:ScreenLine.Tests.CaptureWindowTests "/out:$build\CaptureWindowTests.exe" "/win32manifest:$projectRoot\src\app.manifest" "/resource:$icon,PicRope.Icon" $resources $references $sources (Join-Path $PSScriptRoot 'CaptureWindowTests.cs')
if ($LASTEXITCODE -ne 0) { throw '截图测试构建失败' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\App.config') -Destination (Join-Path $build 'CaptureWindowTests.exe.config')
& (Join-Path $build 'CaptureWindowTests.exe')
if ($LASTEXITCODE -ne 0) { throw '截图测试未通过' }

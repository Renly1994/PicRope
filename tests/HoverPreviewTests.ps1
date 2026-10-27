param([switch]$FlickerProbe, [switch]$Portrait, [switch]$ScrollProbe, [switch]$ClipboardProbe, [switch]$BasketProbe, [switch]$BasketLayout, [switch]$InstalledBasket, [switch]$EditorProbe)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkPath 'csc.exe'
$buildPath = Join-Path $projectRoot 'build'
New-Item -ItemType Directory -Path $buildPath -Force | Out-Null
& (Join-Path $projectRoot 'packaging\prepare-assets.ps1')
$references = @(
    "/r:$frameworkPath\WPF\PresentationCore.dll", "/r:$frameworkPath\WPF\PresentationFramework.dll", "/r:$frameworkPath\WPF\WindowsBase.dll",
    "/r:$frameworkPath\System.Xaml.dll", "/r:$frameworkPath\System.Web.Extensions.dll", "/r:$frameworkPath\Microsoft.VisualBasic.dll",
    "/r:$projectRoot\vendor\WebView2\Microsoft.Web.WebView2.Core.dll", "/r:$projectRoot\vendor\WebView2\Microsoft.Web.WebView2.Wpf.dll",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Net.Http.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll'
)
$resources = @(Get-ChildItem -LiteralPath (Join-Path $buildPath 'runtime-assets') -Filter 'basket-*.png' | ForEach-Object { '/resource:' + $_.FullName + ',PicRope.Basket.' + $_.BaseName.Substring(7) })
$resources += @(Get-ChildItem -LiteralPath (Join-Path $buildPath 'runtime-assets') -Filter 'envelope-*.png' | ForEach-Object { '/resource:' + $_.FullName + ',PicRope.Envelope.' + $_.BaseName.Substring(9) })
$sources = (Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs').FullName
$icon = Join-Path $projectRoot 'src\assets\PicRope.ico'
$testName = if ($FlickerProbe) { 'HoverFlickerProbe' } elseif ($ScrollProbe) { 'HoverScrollTests' } elseif ($ClipboardProbe) { 'ClipboardBasketProbe' } elseif ($BasketProbe) { 'BasketPerformanceTests' } elseif ($BasketLayout) { 'BasketLayoutTests' } elseif ($EditorProbe) { 'EditorResourceProbe' } else { 'HoverPreviewTests' }
if ($EditorProbe) { $references += '/r:System.Management.dll' }
$manifest = if ($ScrollProbe) { "/win32manifest:$projectRoot\src\app.manifest" }
& $compilerPath /nologo /target:exe /platform:x64 /codepage:65001 "/main:ScreenLine.Tests.$testName" "/out:$buildPath\$testName.exe" "/resource:$icon,PicRope.Icon" $manifest $resources $references $sources (Join-Path $PSScriptRoot ($testName + '.cs'))
if ($LASTEXITCODE -ne 0) { throw '悬停预览测试构建失败' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\App.config') -Destination (Join-Path $buildPath ($testName + '.exe.config'))
Copy-Item -LiteralPath (Join-Path $projectRoot 'vendor\WebView2\Microsoft.Web.WebView2.Core.dll'),(Join-Path $projectRoot 'vendor\WebView2\Microsoft.Web.WebView2.Wpf.dll'),(Join-Path $projectRoot 'vendor\WebView2\WebView2Loader.dll') -Destination $buildPath
if ($BasketProbe -and $InstalledBasket) { & (Join-Path $buildPath ($testName + '.exe')) --installed }
elseif ($FlickerProbe -and $Portrait) { & (Join-Path $buildPath ($testName + '.exe')) --portrait }
else { & (Join-Path $buildPath ($testName + '.exe')) }
if ($LASTEXITCODE -ne 0) { throw '悬停预览测试未通过' }

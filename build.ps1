param([switch]$DesktopTests, [switch]$FolderTests, [switch]$MenuTests, [switch]$EditorTests, [switch]$ThemeTests, [switch]$WindTests, [switch]$BasketTests, [switch]$ScrollTests, [switch]$BasketAppearanceTests, [switch]$BasketMotionTests, [switch]$UpdateTests, [switch]$TextTests, [switch]$EnvelopeMotionTests, [switch]$MonitorTests, [switch]$ScreenshotTests, [switch]$Package)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkPath 'csc.exe'
$buildPath = Join-Path $projectRoot 'build'
New-Item -ItemType Directory -Path $buildPath -Force | Out-Null
$references = @(
    "/r:$frameworkPath\WPF\PresentationCore.dll",
    "/r:$frameworkPath\WPF\PresentationFramework.dll",
    "/r:$frameworkPath\WPF\WindowsBase.dll",
    "/r:$frameworkPath\System.Xaml.dll",
    "/r:$frameworkPath\System.Web.Extensions.dll",
    "/r:$frameworkPath\Microsoft.VisualBasic.dll",
    "/r:$projectRoot\vendor\WebView2\Microsoft.Web.WebView2.Core.dll",
    "/r:$projectRoot\vendor\WebView2\Microsoft.Web.WebView2.Wpf.dll",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll'
    '/r:System.Net.Http.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll'
)
$sources = (Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs').FullName
$manifestPath = Join-Path $projectRoot 'src\app.manifest'
$iconPath = Join-Path $projectRoot 'src\assets\PicRope.ico'
$basketResources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src\assets') -Filter 'basket-*.png' | ForEach-Object { '/resource:' + $_.FullName + ',PicRope.Basket.' + $_.BaseName.Substring(7) }
$basketResources += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src\assets') -Filter 'envelope-*.png' | ForEach-Object { '/resource:' + $_.FullName + ',PicRope.Envelope.' + $_.BaseName.Substring(9) })
& $compilerPath /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$buildPath\PicRope.exe" "/win32manifest:$manifestPath" "/win32icon:$iconPath" "/resource:$iconPath,PicRope.Icon" $basketResources $references $sources
if ($LASTEXITCODE -ne 0) { throw '应用构建失败' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\App.config') -Destination (Join-Path $buildPath 'PicRope.exe.config')
Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging\update.ps1') -Destination $buildPath
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE'),(Join-Path $projectRoot 'NOTICE') -Destination $buildPath
$editorPath = Join-Path $buildPath 'editor'
$noticePath = Join-Path $buildPath 'third-party-notices'
New-Item -ItemType Directory -Path $editorPath,(Join-Path $noticePath 'Filerobot'),(Join-Path $noticePath 'WebView2') -Force | Out-Null
Copy-Item -Path (Join-Path $projectRoot 'src\editor\*') -Destination $editorPath
Copy-Item -LiteralPath (Join-Path $projectRoot 'vendor\Filerobot\filerobot-image-editor.min.js') -Destination $editorPath
Copy-Item -LiteralPath (Join-Path $projectRoot 'vendor\Filerobot\LICENSE'),(Join-Path $projectRoot 'vendor\Filerobot\THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $noticePath 'Filerobot')
Copy-Item -LiteralPath (Join-Path $projectRoot 'vendor\WebView2\LICENSE.txt'),(Join-Path $projectRoot 'vendor\WebView2\NOTICE.txt') -Destination (Join-Path $noticePath 'WebView2')
Copy-Item -LiteralPath (Join-Path $projectRoot 'vendor\WebView2\Microsoft.Web.WebView2.Core.dll'),(Join-Path $projectRoot 'vendor\WebView2\Microsoft.Web.WebView2.Wpf.dll'),(Join-Path $projectRoot 'vendor\WebView2\WebView2Loader.dll') -Destination $buildPath
Write-Output ('构建完成：' + (Join-Path $buildPath 'PicRope.exe'))

if (-not ($FolderTests -or $MenuTests -or $EditorTests -or $ThemeTests -or $WindTests -or $EnvelopeMotionTests) -and (Test-Path -LiteralPath (Join-Path $projectRoot 'tests\CoreTests.cs'))) {
    & $compilerPath /nologo /target:exe /codepage:65001 "/out:$buildPath\CoreTests.exe" /main:ScreenLine.Tests.CoreTests $references (Join-Path $projectRoot 'src\Core.cs') (Join-Path $projectRoot 'src\SpringMotion.cs') (Join-Path $projectRoot 'tests\CoreTests.cs')
    if ($LASTEXITCODE -ne 0) { throw '核心测试构建失败' }
    & (Join-Path $buildPath 'CoreTests.exe')
    if ($LASTEXITCODE -ne 0) { throw '核心测试未通过' }
}

if ($DesktopTests -or $MenuTests -or $EditorTests -or $ThemeTests -or $WindTests -or $BasketTests -or $ScrollTests -or $BasketAppearanceTests -or $BasketMotionTests) {
    $desktopReferences = $references + @("/r:$frameworkPath\WPF\UIAutomationClient.dll", "/r:$frameworkPath\WPF\UIAutomationTypes.dll", "/r:$frameworkPath\WPF\UIAutomationProvider.dll")
    & $compilerPath /nologo /target:exe /platform:x64 /codepage:65001 "/out:$buildPath\DesktopTests.exe" /main:ScreenLine.Tests.DesktopTests "/win32manifest:$manifestPath" "/win32icon:$iconPath" "/resource:$iconPath,PicRope.Icon" $basketResources $desktopReferences $sources (Join-Path $projectRoot 'tests\DesktopTests.cs')
    if ($LASTEXITCODE -ne 0) { throw '桌面测试构建失败' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'src\App.config') -Destination (Join-Path $buildPath 'DesktopTests.exe.config')
    if ($BasketMotionTests) { & (Join-Path $buildPath 'DesktopTests.exe') --basket-motion }
    elseif ($BasketAppearanceTests) { & (Join-Path $buildPath 'DesktopTests.exe') --basket-appearance }
    elseif ($ScrollTests) { & (Join-Path $buildPath 'DesktopTests.exe') --scroll-bounds }
    elseif ($BasketTests) { & (Join-Path $buildPath 'DesktopTests.exe') --basket }
    elseif ($WindTests) { & (Join-Path $buildPath 'DesktopTests.exe') --wind }
    elseif ($ThemeTests) { & (Join-Path $buildPath 'DesktopTests.exe') --themes }
    elseif ($MenuTests) { & (Join-Path $buildPath 'DesktopTests.exe') --menus }
    elseif ($EditorTests) { & (Join-Path $buildPath 'DesktopTests.exe') --editor }
    else { & (Join-Path $buildPath 'DesktopTests.exe') }
    if ($LASTEXITCODE -ne 0) { throw '桌面测试未通过' }
}

if ($EditorTests) {
    & $compilerPath /nologo /target:exe /codepage:65001 "/out:$buildPath\EditorFileTests.exe" /main:ScreenLine.Tests.EditorFileTests $references (Join-Path $projectRoot 'src\Core.cs') (Join-Path $projectRoot 'src\ImageEditFiles.cs') (Join-Path $projectRoot 'tests\EditorFileTests.cs')
    if ($LASTEXITCODE -ne 0) { throw '编辑图片文件测试构建失败' }
    & (Join-Path $buildPath 'EditorFileTests.exe')
    if ($LASTEXITCODE -ne 0) { throw '编辑图片文件测试未通过' }
}

if ($FolderTests) {
    & $compilerPath /nologo /target:exe /codepage:65001 "/out:$buildPath\FolderOpenTests.exe" $references (Join-Path $projectRoot 'src\Core.cs') (Join-Path $projectRoot 'src\WindowsShell.cs') (Join-Path $projectRoot 'tests\FolderOpenTests.cs')
    if ($LASTEXITCODE -ne 0) { throw '目录打开测试构建失败' }
    & (Join-Path $buildPath 'FolderOpenTests.exe')
    if ($LASTEXITCODE -ne 0) { throw '目录打开测试未通过' }
}

if ($UpdateTests) {
    & $compilerPath /nologo /target:exe /platform:x64 /codepage:65001 "/out:$buildPath\UpdateTests.exe" /main:ScreenLine.Tests.UpdateTests "/win32manifest:$manifestPath" "/resource:$iconPath,PicRope.Icon" $basketResources $references $sources (Join-Path $projectRoot 'tests\UpdateTests.cs')
    if ($LASTEXITCODE -ne 0) { throw '更新测试构建失败' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'src\App.config') -Destination (Join-Path $buildPath 'UpdateTests.exe.config')
    & (Join-Path $buildPath 'UpdateTests.exe')
    if ($LASTEXITCODE -ne 0) { throw '更新测试未通过' }
}

if ($TextTests) {
    & $compilerPath /nologo /target:exe /platform:x64 /codepage:65001 "/out:$buildPath\TextTests.exe" /main:ScreenLine.Tests.TextTests "/win32manifest:$manifestPath" "/resource:$iconPath,PicRope.Icon" $basketResources $references $sources (Join-Path $projectRoot 'tests\TextTests.cs')
    if ($LASTEXITCODE -ne 0) { throw '文字功能测试构建失败' }
    & (Join-Path $buildPath 'TextTests.exe')
    if ($LASTEXITCODE -ne 0) { throw '文字功能测试未通过' }
}

if ($EnvelopeMotionTests) {
    & $compilerPath /nologo /target:exe /platform:x64 /codepage:65001 "/out:$buildPath\EnvelopeMotionTests.exe" /main:ScreenLine.Tests.EnvelopeMotionTests "/win32manifest:$manifestPath" "/resource:$iconPath,PicRope.Icon" $basketResources $references $sources (Join-Path $projectRoot 'tests\EnvelopeMotionTests.cs')
    if ($LASTEXITCODE -ne 0) { throw '信封动效测试构建失败' }
    & (Join-Path $buildPath 'EnvelopeMotionTests.exe')
    if ($LASTEXITCODE -ne 0) { throw '信封动效测试未通过' }
}

if ($MonitorTests) { & (Join-Path $projectRoot 'tests\MonitorTests.ps1') }

if ($ScreenshotTests) {
    & $compilerPath /nologo /target:exe /platform:x64 /codepage:65001 "/out:$buildPath\ScreenshotTests.exe" /main:ScreenLine.Tests.ScreenshotTests $references (Join-Path $projectRoot 'src\Core.cs') (Join-Path $projectRoot 'src\ScreenshotCapture.cs') (Join-Path $projectRoot 'tests\ScreenshotTests.cs')
    if ($LASTEXITCODE -ne 0) { throw '截图测试构建失败' }
    & (Join-Path $buildPath 'ScreenshotTests.exe')
    if ($LASTEXITCODE -ne 0) { throw '截图测试未通过' }
}

if ($Package) {
    & (Join-Path $projectRoot 'package.ps1')
}

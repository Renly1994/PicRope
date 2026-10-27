param([switch]$CurrentClipboard, [switch]$Worker)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @("/r:$framework\WPF\PresentationCore.dll", "/r:$framework\WPF\PresentationFramework.dll", "/r:$framework\WPF\WindowsBase.dll", "/r:$framework\System.Xaml.dll", "/r:$framework\System.Web.Extensions.dll", '/r:System.dll', '/r:System.Core.dll', '/r:System.Windows.Forms.dll')
$testName = if ($Worker) { 'ClipboardWorkerTests' } else { 'ClipboardImageTests' }
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /codepage:65001 "/out:$root\build\$testName.exe" $references (Join-Path $root 'src\Core.cs') (Join-Path $root 'src\CapturePipeline.cs') (Join-Path $root 'src\WindowsClipboard.cs') (Join-Path $root 'src\WindowsNative.cs') (Join-Path $PSScriptRoot ($testName + '.cs'))
if ($LASTEXITCODE -ne 0) { throw '剪贴板图片识别测试构建失败' }
if ($CurrentClipboard -and -not $Worker) { & (Join-Path $root 'build\ClipboardImageTests.exe') --current-clipboard }
else { & (Join-Path $root ('build\' + $testName + '.exe')) }
if ($LASTEXITCODE -ne 0) { throw '剪贴板图片识别测试未通过' }

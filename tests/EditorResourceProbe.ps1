param([switch]$ExportReview)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @(
    "/r:$framework\WPF\PresentationCore.dll", "/r:$framework\WPF\PresentationFramework.dll",
    "/r:$framework\WPF\WindowsBase.dll", "/r:$framework\System.Xaml.dll",
    "/r:$framework\System.Web.Extensions.dll", "/r:$framework\Microsoft.VisualBasic.dll",
    "/r:$root\vendor\WebView2\Microsoft.Web.WebView2.Core.dll",
    "/r:$root\vendor\WebView2\Microsoft.Web.WebView2.Wpf.dll",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
    '/r:System.Net.Http.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll',
    '/r:System.Management.dll'
)
$sources = (Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs').FullName
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /codepage:65001 "/out:$root\build\EditorResourceProbe.exe" /main:ScreenLine.Tests.EditorResourceProbe "/win32manifest:$root\src\app.manifest" $references $sources (Join-Path $PSScriptRoot 'EditorResourceProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Editor resource probe compilation failed' }
Copy-Item -LiteralPath (Join-Path $root 'src\App.config') -Destination (Join-Path $root 'build\EditorResourceProbe.exe.config')
if ($ExportReview) { & (Join-Path $root 'build\EditorResourceProbe.exe') '--export-review' }
else { & (Join-Path $root 'build\EditorResourceProbe.exe') }
if ($LASTEXITCODE -ne 0) { throw 'Editor resource probe failed' }

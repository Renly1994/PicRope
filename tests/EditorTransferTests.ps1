$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @("/r:$framework\WPF\PresentationCore.dll", "/r:$framework\WPF\PresentationFramework.dll", "/r:$framework\WPF\WindowsBase.dll", "/r:$framework\System.Xaml.dll", "/r:$framework\System.Web.Extensions.dll", '/r:System.dll', '/r:System.Core.dll')
& (Join-Path $framework 'csc.exe') /nologo /target:exe /codepage:65001 "/out:$root\build\EditorTransferTests.exe" /main:ScreenLine.Tests.EditorTransferTests $references (Join-Path $root 'src\Core.cs') (Join-Path $root 'src\ImageEditFiles.cs') (Join-Path $PSScriptRoot 'EditorTransferTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Transfer tests compilation failed' }
& (Join-Path $root 'build\EditorTransferTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Transfer tests failed' }

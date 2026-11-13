$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$output = Join-Path ([System.IO.Path]::GetTempPath()) ('picrope-persistence-' + [guid]::NewGuid().ToString('N') + '.exe')
$references = @("/r:$framework\WPF\PresentationCore.dll", "/r:$framework\WPF\PresentationFramework.dll", "/r:$framework\WPF\WindowsBase.dll", "/r:$framework\System.Xaml.dll", "/r:$framework\System.Web.Extensions.dll", '/r:System.dll', '/r:System.Core.dll', '/r:System.Windows.Forms.dll')
try {
    & (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /codepage:65001 "/out:$output" $references (Join-Path $root 'src\Core.cs') (Join-Path $root 'src\CapturePipeline.cs') (Join-Path $root 'src\WindowsClipboard.cs') (Join-Path $root 'src\WindowsNative.cs') (Join-Path $PSScriptRoot 'CapturePersistenceTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Capture persistence test compilation failed' }
    & $output
    if ($LASTEXITCODE -ne 0) { throw 'Capture persistence regression failed' }
}
finally {
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output }
}

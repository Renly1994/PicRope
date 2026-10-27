param([int]$KeepDays = 7)
$ErrorActionPreference = 'Stop'
$buildRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\build'))
$testRoot = Join-Path $buildRoot 'test-data'
if (-not (Test-Path -LiteralPath $testRoot)) { return }
if (Get-Process -Name CoreTests,DesktopTests,TextTests,ReleaseTests,UpdateTests,EditorFileTests,BasketLayoutTests,MultiMonitorChecks -ErrorAction SilentlyContinue) { throw '验证程序正在运行，暂不清理测试数据。' }
$removedBytes = 0L
foreach ($directory in Get-ChildItem -LiteralPath $testRoot -Directory) {
    if ($directory.Name -notmatch '^(core|desktop|texts|restore|updates|release|layout|editor-files)-[0-9a-f]{32}$') { continue }
    if ($directory.LastWriteTimeUtc -gt [DateTime]::UtcNow.AddDays(-$KeepDays)) { continue }
    $target = [IO.Path]::GetFullPath($directory.FullName)
    if (-not $target.StartsWith($testRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '测试清理路径超出范围。' }
    if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
    $entries = @(Get-ChildItem -LiteralPath $target -Recurse -Force)
    if ($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { continue }
    $removedBytes += ($entries | Where-Object { -not $_.PSIsContainer } | Measure-Object Length -Sum).Sum
    Remove-Item -LiteralPath $target -Recurse -Force
}
Write-Output ('已清理生成的测试数据：{0:N1} MB' -f ($removedBytes / 1MB))

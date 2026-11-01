param([string]$NativeSource, [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$RunName = 'results')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureRoot = Join-Path $projectRoot 'build\caption-button-tests'
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
$nativePath = if ($NativeSource) { [IO.Path]::GetFullPath($NativeSource) } else { Join-Path $projectRoot 'src\WindowsNative.cs' }
$original = [IO.File]::ReadAllText($nativePath)
$native = $original

function MethodText([string]$source, [string]$signature) {
    $start = $source.IndexOf($signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw ('Missing method: ' + $signature) }
    $open = $source.IndexOf('{', $start)
    $depth = 0
    for ($index = $open; $index -lt $source.Length; $index++) {
        if ($source[$index] -eq '{') { $depth++ }
        elseif ($source[$index] -eq '}') {
            $depth--
            if ($depth -eq 0) { return $source.Substring($start, $index - $start + 1) }
        }
    }
    throw ('Unclosed method: ' + $signature)
}

function ReplaceBoundary([string]$before, [string]$after) {
    if ($script:native.Split([string[]]@($before), [StringSplitOptions]::None).Count -ne 2) {
        throw ('Boundary must match once: ' + $before)
    }
    $script:native = $script:native.Replace($before, $after)
}

$fixture = 'ScreenLine.Tests.Snapshot'
ReplaceBoundary '[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();' "public static IntPtr GetForegroundWindow() { return $fixture.Foreground; }"
ReplaceBoundary '[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rectangle rectangle);' "public static bool GetWindowRect(IntPtr window, out Rectangle rectangle) { return $fixture.GetRectangle(window, out rectangle); }"
ReplaceBoundary '[DllImport("user32.dll")] public static extern IntPtr GetShellWindow();' "public static IntPtr GetShellWindow() { return $fixture.Shell; }"
ReplaceBoundary '[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int length);' "static int GetClassName(IntPtr window, StringBuilder name, int length) { return $fixture.GetClass(window, name, length); }"
ReplaceBoundary '[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);' "public static bool IsWindowVisible(IntPtr window) { return $fixture.Visible(window); }"
ReplaceBoundary '[DllImport("user32.dll")] static extern bool EnumWindows(WindowEnumerator callback, IntPtr parameter);' "static bool EnumWindows(WindowEnumerator callback, IntPtr parameter) { foreach (var item in $fixture.Windows) { $fixture.Enumerated.Add(item.Handle); if (!callback(item.Handle, parameter)) { $fixture.Selected = item.Handle; break; } } return true; }"
ReplaceBoundary '[DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);' "static bool IsIconic(IntPtr window) { return $fixture.Iconic(window); }"
ReplaceBoundary '[DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);' "static int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size) { return $fixture.GetCloaked(window, out value); }"
ReplaceBoundary '[DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);' "static int GetWindowLong(IntPtr window, int index) { return $fixture.Style(window); }"
$regionBoundaries = @{
    CreateRectRgn = "static IntPtr CreateRectRgn(int left, int top, int right, int bottom) { return $fixture.CreateRegion(left, top, right, bottom); }"
    GetWindowRgn = "static int GetWindowRgn(IntPtr window, IntPtr region) { return $fixture.GetRegion(window, region); }"
    PtInRegion = "static bool PtInRegion(IntPtr region, int x, int y) { return $fixture.PointInRegion(region, x, y); }"
    DeleteObject = "static bool DeleteObject(IntPtr region) { return $fixture.DeleteRegion(region); }"
}
foreach ($name in $regionBoundaries.Keys) {
    $pattern = '(?m)^[ \t]*\[DllImport\("(?:gdi32|user32)\.dll"[^\r\n]*\]\s*(?:(?:public|private|internal)\s+)?static extern \w+ ' + $name + '\([^\r\n]*\);'
    $matches = [regex]::Matches($native, $pattern)
    if ($matches.Count -eq 1) { ReplaceBoundary $matches[0].Value $regionBoundaries[$name] }
    elseif ($matches.Count -ne 0 -or $native -match ('\b' + $name + '\s*\(')) { throw ('Unrecognized region boundary: ' + $name) }
}
$scaleMethod = MethodText $original 'public static double MonitorScale(Forms.Screen screen)'
ReplaceBoundary $scaleMethod "public static double MonitorScale(Forms.Screen screen) { return $fixture.Scales[screen.DeviceName]; }"

$integrity = @()
foreach ($signature in @('public static bool OverCaptionButtons(Forms.Screen screen, Point cursor, IntPtr overlay)', 'public static bool FullScreen(Forms.Screen screen, IntPtr overlay)')) {
    $before = MethodText $original $signature
    $after = MethodText $native $signature
    if (-not [string]::Equals($before, $after, [StringComparison]::Ordinal)) { throw ('Production method changed: ' + $signature) }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($before))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $integrity += ('UNCHANGED ' + $signature + ' SHA256=' + $hash)
}
$integrityPath = Join-Path $fixtureRoot ($RunName + '-method-integrity.log')
$copyPath = Join-Path $fixtureRoot ($RunName + '-WindowsNative.FixtureCopy.cs')
[IO.File]::WriteAllText($integrityPath, ($integrity -join [Environment]::NewLine) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($copyPath, $native, [Text.UTF8Encoding]::new($false))
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', "/r:$framework\WPF\PresentationCore.dll", "/r:$framework\WPF\PresentationFramework.dll", "/r:$framework\WPF\WindowsBase.dll", "/r:$framework\System.Xaml.dll")
$executable = Join-Path $fixtureRoot ($RunName + '-CaptionButtonTests.exe')
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /codepage:65001 /main:ScreenLine.Tests.CaptionButtonTests "/out:$executable" $references $copyPath (Join-Path $PSScriptRoot 'CaptionButtonTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed' }
& $executable 2>&1 | Tee-Object -FilePath (Join-Path $fixtureRoot ($RunName + '.log'))
if ($LASTEXITCODE -ne 0) { throw 'Fixture regression failed' }
$integrity

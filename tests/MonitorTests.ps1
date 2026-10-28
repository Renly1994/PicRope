$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputPath = Join-Path $projectRoot 'build\monitor-tests'
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkPath 'csc.exe'
$simulation = 'ScreenLine.Tests.MultiMonitorChecks'
$native = [IO.File]::ReadAllText((Join-Path $projectRoot 'src\WindowsNative.cs'))

# 仅在隔离测试副本中替换系统边界，保留图绳的原始业务代码。
$replacements = @(
    @('public static Forms.Screen[] Screens { get { return Forms.Screen.AllScreens; } }', "public static Forms.Screen[] Screens { get { return $simulation.Simulating ? $simulation.Screens : Forms.Screen.AllScreens; } }"),
    @('public static Point Cursor()', "public static Point Cursor() { return $simulation.Simulating ? $simulation.Pointer : OriginalCursor(); }`n        static Point OriginalCursor()"),
    @('public static Forms.Screen ScreenAtPointer()', "public static Forms.Screen ScreenAtPointer() { return $simulation.Simulating ? $simulation.ScreenAtPointer() : OriginalScreenAtPointer(); }`n        static Forms.Screen OriginalScreenAtPointer()"),
    @('public static double MonitorScale(Forms.Screen screen)', "public static double MonitorScale(Forms.Screen screen) { return $simulation.Simulating ? $simulation.Scales[screen.DeviceName] : OriginalMonitorScale(screen); }`n        static double OriginalMonitorScale(Forms.Screen screen)"),
    @('public static bool OverCaptionButtons(Forms.Screen screen, Point cursor, IntPtr overlay)', "public static bool OverCaptionButtons(Forms.Screen screen, Point cursor, IntPtr overlay) { return $simulation.Simulating ? false : OriginalOverCaptionButtons(screen, cursor, overlay); }`n        static bool OriginalOverCaptionButtons(Forms.Screen screen, Point cursor, IntPtr overlay)"),
    @('public static bool FullScreen(Forms.Screen screen, IntPtr overlay)', "public static bool FullScreen(Forms.Screen screen, IntPtr overlay) { return $simulation.Simulating ? $simulation.FullScreens.Contains(screen.DeviceName) : OriginalFullScreen(screen, overlay); }`n        static bool OriginalFullScreen(Forms.Screen screen, IntPtr overlay)"),
    @('[DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);', "[DllImport(`"user32.dll`", EntryPoint = `"GetAsyncKeyState`")] static extern short OriginalGetAsyncKeyState(int key);`n        public static short GetAsyncKeyState(int key) { return $simulation.Simulating ? (short)($simulation.MouseDown ? -32768 : 0) : OriginalGetAsyncKeyState(key); }"),
    @('[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);', "[DllImport(`"user32.dll`", EntryPoint = `"SetWindowPos`")] static extern bool OriginalSetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);`n        public static bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags) { return $simulation.Simulating ? $simulation.SetWindowPos(window, x, y, width, height) : OriginalSetWindowPos(window, after, x, y, width, height, flags); }"),
    @('[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rectangle rectangle);', "[DllImport(`"user32.dll`", EntryPoint = `"GetWindowRect`")] static extern bool OriginalGetWindowRect(IntPtr window, out Rectangle rectangle);`n        public static bool GetWindowRect(IntPtr window, out Rectangle rectangle) { if ($simulation.Simulating && $simulation.Positions.TryGetValue(window, out rectangle)) return true; return OriginalGetWindowRect(window, out rectangle); }")
)
foreach ($replacement in $replacements) {
    if ($native.Split([string[]]@($replacement[0]), [StringSplitOptions]::None).Count -ne 2) { throw ('系统边界签名已变化：' + $replacement[0]) }
    $native = $native.Replace($replacement[0], $replacement[1])
}
$nativePath = Join-Path $outputPath 'WindowsNative.TestCopy.cs'
[IO.File]::WriteAllText($nativePath, $native, [Text.UTF8Encoding]::new($false))
$references = @(
    "/r:$frameworkPath\WPF\PresentationCore.dll", "/r:$frameworkPath\WPF\PresentationFramework.dll",
    "/r:$frameworkPath\WPF\WindowsBase.dll", "/r:$frameworkPath\System.Xaml.dll",
    "/r:$frameworkPath\System.Web.Extensions.dll", "/r:$frameworkPath\Microsoft.VisualBasic.dll",
    "/r:$projectRoot\vendor\WebView2\Microsoft.Web.WebView2.Core.dll",
    "/r:$projectRoot\vendor\WebView2\Microsoft.Web.WebView2.Wpf.dll",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
    '/r:System.Net.Http.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll'
)
$sources = (Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | Where-Object Name -ne 'WindowsNative.cs').FullName
$iconPath = Join-Path $projectRoot 'src\assets\PicRope.ico'
$resources = @("/resource:$iconPath,PicRope.Icon")
& (Join-Path $projectRoot 'packaging\prepare-assets.ps1')
foreach ($prefix in @('basket', 'envelope')) {
    $resourceName = if ($prefix -eq 'basket') { 'Basket' } else { 'Envelope' }
    $resources += Get-ChildItem -LiteralPath (Join-Path $projectRoot 'build\runtime-assets') -Filter "$prefix-*.png" | ForEach-Object { '/resource:' + $_.FullName + ',PicRope.' + $resourceName + '.' + $_.BaseName.Substring($prefix.Length + 1) }
}
$executablePath = Join-Path $outputPath 'MultiMonitorChecks.exe'
& $compilerPath /nologo /target:exe /platform:x64 /codepage:65001 /main:ScreenLine.Tests.MultiMonitorChecks "/out:$executablePath" "/win32manifest:$projectRoot\src\app.manifest" $references $resources $sources $nativePath (Join-Path $PSScriptRoot 'MonitorTests.cs')
if ($LASTEXITCODE -ne 0) { throw '多屏验证程序编译失败' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\App.config') -Destination ($executablePath + '.config')
& $executablePath
if ($LASTEXITCODE -ne 0) { throw '多屏验证存在未通过项目' }

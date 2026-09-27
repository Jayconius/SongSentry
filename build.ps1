# Builds SongSentry with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK or Visual Studio needed.
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path "$fw\csc.exe")) { $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$csc = "$fw\csc.exe"
$wm = Join-Path $env:WINDIR 'System32\WinMetadata'

# /codepage:65001 = sources are UTF-8 (otherwise csc assumes the ANSI code page and garbles symbols like arrows and ellipses).
# Now Playing uses the WinRT metadata that ships with Windows. Deliberately NOT System.Runtime.WindowsRuntime.dll:
# its AsTask() needs the union Windows.winmd, which Windows doesn't ship.
$refs = '/codepage:65001', '/nologo', '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
        '/r:System.Web.Extensions.dll', '/r:System.Security.dll', '/r:Microsoft.VisualBasic.dll', '/unsafe',
        "/r:$wm\Windows.Media.winmd", "/r:$wm\Windows.Foundation.winmd",
        "/r:$fw\System.Runtime.dll", "/r:$fw\System.Runtime.InteropServices.WindowsRuntime.dll", "/r:$fw\System.Threading.Tasks.dll"
$core = 'Util', 'Settings', 'Obs', 'NowPlaying', 'MusicBrainz', 'Engine', 'AudioDevices', 'Landmark', 'SongLibrary', 'Capture', 'MusicDetect', 'AudD', 'AudioTag', 'AcoustId', 'Risk', 'SafeLists', 'Playlists', 'Recognizer' | ForEach-Object { "$root\src\$_.cs" }
$ui = 'Theme', 'MainForm', 'Program' | ForEach-Object { "$root\src\$_.cs" }

New-Item -ItemType Directory -Force "$root\build", "$root\dist" | Out-Null

Write-Host '> Running logic tests' -ForegroundColor Cyan
& $csc /target:exe /out:"$root\build\LogicTest.exe" $refs "$root\tools\LogicTest.cs" "$root\tools\FakeObs.cs" $core
if ($LASTEXITCODE) { throw 'LogicTest compile failed' }
& "$root\build\LogicTest.exe"
if ($LASTEXITCODE) { throw 'Logic tests failed' }

Write-Host '> Rendering icon' -ForegroundColor Cyan
& $csc /target:exe /out:"$root\build\MakeAssets.exe" $refs "$root\tools\MakeAssets.cs" "$root\src\Theme.cs"
if ($LASTEXITCODE) { throw 'MakeAssets compile failed' }
& "$root\build\MakeAssets.exe" $root
if ($LASTEXITCODE) { throw 'MakeAssets failed' }

Write-Host '> Building SongSentry.exe (embeds Chromaprint fpcalc, LGPL 2.1, see THIRD_PARTY_NOTICES.md)' -ForegroundColor Cyan
& $csc /target:winexe /optimize+ /platform:anycpu /out:"$root\dist\SongSentry.exe" `
    /win32icon:"$root\assets\icon.ico" /win32manifest:"$root\src\app.manifest" $refs `
    "/resource:$root\third_party\chromaprint\fpcalc.exe,fpcalc.exe" "$root\src\AssemblyInfo.cs" $core $ui
if ($LASTEXITCODE) { throw 'App compile failed' }

Write-Host '> Rendering README screenshots and graphics (fake OBS, made-up songs)' -ForegroundColor Cyan
& $csc /target:exe /main:SongSentry.Screens /out:"$root\build\Screens.exe" $refs "$root\tools\Screens.cs" "$root\tools\Graphics.cs" "$root\tools\FakeObs.cs" $core $ui
if ($LASTEXITCODE) { throw 'Screens compile failed' }
& "$root\build\Screens.exe" "$root\docs"
if ($LASTEXITCODE) { throw 'Screens failed' }

Get-Item "$root\dist\SongSentry.exe" | Select-Object Name, Length

<#
  Builds Celestium Emulator and updates an installed copy in place.

  The .exe files are kept byte-identical between versions (see the csproj), so an update normally only
  replaces the .dll files and antivirus that already trusts the .exe (Norton CyberCapture) doesn't re-check it.
  This script tells you when that isn't the case.

  Usage:  powershell -ExecutionPolicy Bypass -File update.ps1 [-InstallDir <folder>]
#>
param(
    [string]$InstallDir = 'S:\AI\Emulator\CelestiumEmulator'
)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dist = Join-Path $src 'dist'

# 1. Note whether the app is running. Its files are in use, so they get renamed aside (Windows allows that)
#    instead of overwritten, and the new version loads the next time it starts.
$running = Get-Process CelestiumEmulator, celestium -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) }
Get-ChildItem $InstallDir -Filter '*.old' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

# 2. Build.
Remove-Item -Recurse -Force (Join-Path $src 'bin'), (Join-Path $src 'obj'), (Join-Path $src 'cli\bin'), (Join-Path $src 'cli\obj'), $dist -ErrorAction SilentlyContinue
# Build from the source folder: global.json (which pins the SDK, and with it the exact .exe bytes) is only
# honoured when it's in the current directory or one of its parents.
Push-Location $src
try {
    foreach ($proj in 'CelestiumEmulator.csproj', 'cli\celestium.csproj') {
        dotnet publish $proj -c Release -r win-x64 --self-contained false -o $dist -nologo -v q --disable-build-servers
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $proj" }
    }
} finally { Pop-Location }

# 3. Compare the .exe files with the installed ones.
New-Item -ItemType Directory -Force $InstallDir | Out-Null
$exeChanged = @()
foreach ($exe in 'CelestiumEmulator.exe', 'celestium.exe') {
    $old = Join-Path $InstallDir $exe
    if ((Test-Path $old) -and (Get-FileHash $old).Hash -ne (Get-FileHash (Join-Path $dist $exe)).Hash) { $exeChanged += $exe }
}

# 4. Copy everything except debug symbols. Keep the user's launcher.json.
foreach ($file in Get-ChildItem $dist -File | Where-Object { $_.Extension -ne '.pdb' }) {
    $target = Join-Path $InstallDir $file.Name
    if ($file.Name -eq 'launcher.json' -and (Test-Path $target)) { continue }
    if ((Test-Path $target) -and (Get-FileHash $target).Hash -eq (Get-FileHash $file.FullName).Hash) { continue } # unchanged
    try { Copy-Item $file.FullName $target -Force -ErrorAction Stop }
    catch {
        # In use by the running app: move it aside and put the new file in its place.
        Move-Item $target "$target.$([DateTime]::Now.Ticks).old" -Force
        Copy-Item $file.FullName $target -Force
    }
}
# Safety net: every file the app needs must be present before we report success.
$missing = Get-ChildItem $dist -File | Where-Object { $_.Extension -ne '.pdb' -and -not (Test-Path (Join-Path $InstallDir $_.Name)) }
if ($missing) { throw "Update incomplete, missing: $($missing.Name -join ', ')" }

$version = (Select-String -Path (Join-Path $src 'Core.cs') -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
Write-Host "Updated $InstallDir to Celestium Emulator $version." -ForegroundColor Green
if ($running) { Write-Host "Celestium is running: restart it (tray icon > Quit, then open it again) to load the new version." -ForegroundColor Yellow }
if ($exeChanged) {
    Write-Host "Note: $($exeChanged -join ', ') changed, so Norton may check it once more." -ForegroundColor Yellow
} else {
    Write-Host "The .exe files are unchanged, so Norton has nothing new to check."
}

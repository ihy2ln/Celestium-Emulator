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

# 1. Refuse to touch files that are in use. Ask instead of killing anything.
$running = Get-Process CelestiumEmulator, celestium -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    Write-Host "Celestium Emulator is running from $InstallDir." -ForegroundColor Yellow
    Write-Host "Right-click its tray icon and choose Quit (your Android instances keep running), then run this again."
    exit 1
}

# 2. Build.
Remove-Item -Recurse -Force (Join-Path $src 'bin'), (Join-Path $src 'obj'), (Join-Path $src 'cli\bin'), (Join-Path $src 'cli\obj'), $dist -ErrorAction SilentlyContinue
foreach ($proj in 'CelestiumEmulator.csproj', 'cli\celestium.csproj') {
    dotnet publish (Join-Path $src $proj) -c Release -r win-x64 --self-contained false -o $dist -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $proj" }
}

# 3. Compare the .exe files with the installed ones.
New-Item -ItemType Directory -Force $InstallDir | Out-Null
$exeChanged = @()
foreach ($exe in 'CelestiumEmulator.exe', 'celestium.exe') {
    $old = Join-Path $InstallDir $exe
    if ((Test-Path $old) -and (Get-FileHash $old).Hash -ne (Get-FileHash (Join-Path $dist $exe)).Hash) { $exeChanged += $exe }
}

# 4. Copy everything except debug symbols. Keep the user's launcher.json.
Get-ChildItem $dist -File | Where-Object { $_.Extension -ne '.pdb' } | ForEach-Object {
    $target = Join-Path $InstallDir $_.Name
    if ($_.Name -eq 'launcher.json' -and (Test-Path $target)) { return }
    Copy-Item $_.FullName $target -Force
}

$version = (Select-String -Path (Join-Path $src 'Core.cs') -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
Write-Host "Updated $InstallDir to Celestium Emulator $version." -ForegroundColor Green
if ($exeChanged) {
    Write-Host "Note: $($exeChanged -join ', ') changed, so Norton may check it once more." -ForegroundColor Yellow
} else {
    Write-Host "The .exe files are unchanged, so Norton has nothing new to check."
}

# Builds the rttsh setup package.
#
# Usage (from the installer directory):
#   .\build-installer.ps1                 # framework-dependent -> rttsh-<version>-framework-setup.exe
#   .\build-installer.ps1 -SelfContained  # self-contained win-x64 -> rttsh-<version>-selfcontained-setup.exe

param(
    [string]$Configuration = "Release",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$installerDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $installerDir

$flavor = "framework"
# -r win-x64 on BOTH flavors promotes rid-specific assets (native lua54) flat
# into the output root, so {app} carries no runtimes\ tree.
$publishArgs = @("-r", "win-x64", "--self-contained", "false", "-p:DebugType=none")
if ($SelfContained) {
    $flavor = "selfcontained"
    $publishArgs = @("-r", "win-x64", "--self-contained", "true", "-p:DebugType=none")
}

$project = Join-Path $repoRoot "src\RttSh\RttSh.csproj"
$publishRoot = Join-Path $installerDir "publish"
$output = Join-Path $publishRoot "App"

# Clean the shared output so switching flavors (framework/self-contained)
# never leaves stale files behind. Guard: only delete inside installer\publish.
if (Test-Path $output) {
    $resolved = (Resolve-Path -LiteralPath $output).Path
    $resolvedRoot = (Resolve-Path -LiteralPath $publishRoot).Path
    if (-not $resolved.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete unexpected path: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

dotnet publish $project -c $Configuration -o $output @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }

# Locate Inno Setup compiler: PATH first, then common install locations.
$iscc = $null
try {
    $iscc = (Get-Command iscc -ErrorAction Stop).Source
} catch {
}

if (-not $iscc) {
    $isccCandidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        (Join-Path $env:USERPROFILE "scoop\apps\inno-setup\current\ISCC.exe")
    )
    $iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $iscc) {
    throw "Inno Setup 6 (ISCC.exe) not found. Install it first, e.g.: scoop install inno-setup"
}

Write-Host "Compiling installer with: $iscc"
& $iscc "/DFlavor=$flavor" (Join-Path $installerDir "RttSh.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed" }

Write-Host ""
Write-Host "Installer created in: $(Join-Path $installerDir 'dist')" -ForegroundColor Green

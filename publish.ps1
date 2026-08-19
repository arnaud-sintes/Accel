#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publishes Accel as a self-contained, single-file Windows x64 executable, then packages it for
    redistribution: a portable zip always, and a proper Setup.exe (via Inno Setup) when available.

.DESCRIPTION
    Wraps `dotnet publish` with the correct flags for a standalone accel.exe. Idempotent — can be
    run multiple times. Redistribution artifacts (both the zip and, when built, the installer) land
    in dist\, named with the version read from accel.csproj's <Version> (the single source of truth
    for Accel's own version - see App/Controls/AppVersionInfo.cs).

    The Setup.exe step needs Inno Setup 6 (https://jrsoftware.org/isinfo.php) installed locally -
    ISCC.exe is looked up on PATH and at its default install location. If it isn't found, that step
    is skipped with a warning (not a failure) and only the zip is produced - same "best-effort,
    never abort the whole run over an optional step" spirit as the app's own tolerant-path code.

.EXAMPLE
    .\publish.ps1
#>

param()

$publishDir = Join-Path $PSScriptRoot "bin\Release\net8.0-windows\win-x64\publish"
$exePath = Join-Path $publishDir "accel.exe"
$distDir = Join-Path $PSScriptRoot "dist"
$csprojPath = Join-Path $PSScriptRoot "accel.csproj"

function Get-AccelVersion {
    $csprojText = Get-Content -Path $csprojPath -Raw
    $match = [regex]::Match($csprojText, '<Version>([^<]+)</Version>')
    if (-not $match.Success) {
        Write-Warning "No <Version> found in accel.csproj - falling back to 0.0.0"
        return "0.0.0"
    }
    return $match.Groups[1].Value.Trim()
}

function Find-InnoSetupCompiler {
    $onPath = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($onPath) {
        return $onPath.Source
    }

    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    return $null
}

Write-Host "Publishing Accel to $publishDir..."

try {
    & dotnet publish (Join-Path $PSScriptRoot "accel.csproj") `
        -r win-x64 `
        -c Release `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -NoLogo `
        -v minimal

    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
        exit 1
    }

    if (-not (Test-Path $exePath)) {
        Write-Error "Expected executable not found: $exePath"
        exit 1
    }

    $size = (Get-Item $exePath).Length
    $sizeMB = [math]::Round($size / 1MB, 1)
    Write-Host "Success! Published executable: $exePath ($sizeMB MB)"

    $version = Get-AccelVersion
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null

    # --- Portable zip: the same file set the installer below packages (accel.exe + the WebView2
    # terminal panel's loose asset folder), staged into its own folder first so Compress-Archive
    # doesn't also pick up .pdb/xml doc comments/global.json/web.config from the raw publish
    # output - none of those are needed at runtime. No folder.json is shipped: the root-folders
    # config always lives (and is created on demand) at %USERPROFILE%\.claude\accel-folders.json,
    # which is writable without elevation wherever the exe itself was unpacked. ---
    $stageDir = Join-Path $PSScriptRoot "bin\Release\net8.0-windows\win-x64\publish-stage"
    if (Test-Path $stageDir) {
        Remove-Item $stageDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $stageDir | Out-Null
    Copy-Item (Join-Path $publishDir "accel.exe") $stageDir
    Copy-Item (Join-Path $publishDir "wwwroot") $stageDir -Recurse

    $zipPath = Join-Path $distDir "Accel-$version-win-x64.zip"
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }
    Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath
    Write-Host "Packaged portable zip: $zipPath"

    # --- Setup.exe via Inno Setup, when available. ---
    $iscc = Find-InnoSetupCompiler
    if ($iscc) {
        Write-Host "Building installer with $iscc..."
        & $iscc "/DMyAppVersion=$version" (Join-Path $PSScriptRoot "installer\accel.iss")

        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Inno Setup compilation failed with exit code $LASTEXITCODE - zip above is still available."
        }
        else {
            $setupPath = Join-Path $distDir "Accel-Setup-$version.exe"
            if (Test-Path $setupPath) {
                Write-Host "Packaged installer: $setupPath"
            }
            else {
                Write-Warning "Inno Setup reported success but $setupPath was not found."
            }
        }
    }
    else {
        Write-Warning "Inno Setup 6 (ISCC.exe) not found - skipping Setup.exe. Install it from https://jrsoftware.org/isinfo.php to also build one; the portable zip above is still a complete redistributable."
    }

    exit 0
}
catch {
    Write-Error $_
    exit 1
}

#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publishes Accel as a self-contained, single-file Windows x64 executable.

.DESCRIPTION
    Wraps `dotnet publish` with the correct flags for a standalone Accel.exe.
    Idempotent — can be run multiple times.

.EXAMPLE
    .\publish.ps1
#>

param()

$publishDir = Join-Path $PSScriptRoot "bin\Release\net8.0-windows\win-x64\publish"
$exePath = Join-Path $publishDir "accel.exe"

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
    exit 0
}
catch {
    Write-Error $_
    exit 1
}

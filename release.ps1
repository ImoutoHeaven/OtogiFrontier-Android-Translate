[CmdletBinding()]
param(
    [string]$Serial = "127.0.0.1:5555",
    [string]$AdbPath = "adb",
    [string]$InputApk,
    [switch]$CreateKey
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$adb = (Get-Command $AdbPath -ErrorAction Stop).Source
if ($InputApk) {
    $sourceApk = if ([IO.Path]::IsPathRooted($InputApk)) {
        $InputApk
    } else {
        Join-Path $PSScriptRoot $InputApk
    }
    if (-not (Test-Path -LiteralPath $sourceApk -PathType Leaf)) {
        throw "Input APK not found: $sourceApk"
    }
    $sourceApk = (Resolve-Path -LiteralPath $sourceApk).Path
} else {
    & $adb connect $Serial | Out-Null
    $deviceState = ((& $adb -s $Serial get-state 2>$null) -join "").Trim()
    if ($deviceState -ne "device") { throw "ADB device is not ready: $Serial" }

    $paths = @(
        & $adb -s $Serial shell pm path jp.co.dmm.dmmgames.kms |
            ForEach-Object { ($_ -replace '^package:', '').Trim() } |
            Where-Object { $_ }
    )
    if ($paths.Count -ne 1) {
        throw "Expected one official APK, found $($paths.Count); split APKs are unsupported"
    }

    $inputDir = Join-Path $PSScriptRoot "input"
    $sourceApk = Join-Path $inputDir "Original.apk"
    New-Item -ItemType Directory -Force -Path $inputDir | Out-Null
    & $adb -s $Serial pull $paths[0] $sourceApk
    if ($LASTEXITCODE -ne 0) { throw "Failed to pull the official APK" }
}

& (Join-Path $PSScriptRoot "run.ps1") -InputApk $sourceApk -CreateKey:$CreateKey
& (Join-Path $PSScriptRoot "test-loader.ps1") -Serial $Serial -AdbPath $adb

Write-Host "PASS source verification, build, install, and runtime smoke test"

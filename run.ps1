[CmdletBinding()]
param(
    [string]$InputApk = $(if ($env:OTOGI_INPUT_APK) { $env:OTOGI_INPUT_APK } else { "input/Original.apk" }),
    [string]$OutputApk = "Original.prototype-signed.apk",
    [string]$PackageName = "jp.co.dmm.dmmgames.kms.prototype",
    [string]$Image = "otogi-apk-patcher:prototype",
    [switch]$CreateKey
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$inputPath = if ([IO.Path]::IsPathRooted($InputApk)) {
    $InputApk
} else {
    Join-Path $PSScriptRoot $InputApk
}
if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) {
    throw "Input APK not found: $inputPath"
}
$inputPath = (Resolve-Path -LiteralPath $inputPath).Path
$inputDir = Split-Path -Parent $inputPath
$inputName = Split-Path -Leaf $inputPath
$outputDir = Join-Path $PSScriptRoot "out"
$keysDir = Join-Path $PSScriptRoot "keys"
$payloadDir = Join-Path $PSScriptRoot "payload"

if ((Split-Path -Leaf $OutputApk) -ne $OutputApk) {
    throw "OutputApk must be a filename"
}

New-Item -ItemType Directory -Force -Path $outputDir, $keysDir, $payloadDir | Out-Null

& docker build --tag $Image $PSScriptRoot
if ($LASTEXITCODE -ne 0) {
    throw "Docker image build failed with exit code $LASTEXITCODE"
}

$dockerArgs = @(
    "run",
    "--rm",
    "--mount", "type=bind,source=$inputDir,target=/input,readonly",
    "--mount", "type=bind,source=$payloadDir,target=/payload,readonly",
    "--mount", "type=bind,source=$outputDir,target=/output",
    "--mount", "type=bind,source=$keysDir,target=/keys",
    "--env", "INPUT_APK=/input/$inputName",
    "--env", "OUTPUT_APK=$OutputApk",
    "--env", "PACKAGE_NAME=$PackageName",
    "--env", "CREATE_KEY=$($CreateKey.IsPresent.ToString().ToLowerInvariant())",
    $Image
)

& docker @dockerArgs
if ($LASTEXITCODE -ne 0) {
    throw "APK patch/sign container failed with exit code $LASTEXITCODE"
}

$outputPath = Join-Path $outputDir $OutputApk
$pluginPath = Join-Path $outputDir "OtogiTranslate.dll"
if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
    throw "Container succeeded without producing $outputPath"
}
if (-not (Test-Path -LiteralPath $pluginPath -PathType Leaf)) {
    throw "Container succeeded without producing $pluginPath"
}
Write-Host "Verified artifacts: $outputPath, $pluginPath"

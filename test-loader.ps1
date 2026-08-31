[CmdletBinding()]
param(
    [string]$Serial = "127.0.0.1:5555",
    [string]$AdbPath = "adb",
    [string]$Apk = "out/Original.prototype-signed.apk",
    [string]$PackageName = "jp.co.dmm.dmmgames.kms.prototype",
    [int]$AssetCopyTimeoutSeconds = 180,
    [int]$PluginLoadTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$adb = (Get-Command $AdbPath -ErrorAction Stop).Source
$apkPath = if ([IO.Path]::IsPathRooted($Apk)) {
    $Apk
} else {
    Join-Path $PSScriptRoot $Apk
}
$pluginPath = Join-Path $PSScriptRoot "out/OtogiTranslate.dll"
$externalFiles = "/sdcard/Android/data/$PackageName/files"
$loaderLog = "$externalFiles/melonloader/etc/Latest.log"
$expectedFontSha256 = "929faeecb6a0bd636b92a921d2d590350b72e301832272302a064f3d5a0ab893"

if (-not (Test-Path -LiteralPath $apkPath -PathType Leaf)) {
    throw "APK not found: $apkPath"
}
if (-not (Test-Path -LiteralPath $pluginPath -PathType Leaf)) {
    throw "Plugin not found: $pluginPath"
}
$expectedPluginSha256 = (Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash.ToLowerInvariant()

& $adb connect $Serial | Out-Null
$deviceState = ((& $adb -s $Serial get-state 2>$null) -join "").Trim()
if ($deviceState -ne "device") { throw "ADB device is not ready: $Serial" }

& $adb -s $Serial install -r $apkPath
if ($LASTEXITCODE -ne 0) { throw "APK install failed" }
& $adb -s $Serial shell appops set $PackageName MANAGE_EXTERNAL_STORAGE allow
if ($LASTEXITCODE -ne 0) { throw "Failed to grant all-files access" }
& $adb -s $Serial shell "mkdir -p $externalFiles && printf enabled > $externalFiles/isEmulator.txt"
if ($LASTEXITCODE -ne 0) { throw "Failed to enable LemonLoader emulator mode" }

# Refresh build-owned files without deleting login, config, or translation cache.
& $adb -s $Serial shell am force-stop $PackageName
& $adb -s $Serial shell "rm -f $externalFiles/Plugins/OtogiTranslate.dll $externalFiles/Assets/font $externalFiles/Assets/font.md5 $loaderLog"
if ($LASTEXITCODE -ne 0) { throw "Failed to remove stale runtime assets" }
& $adb -s $Serial shell monkey -p $PackageName -c android.intent.category.LAUNCHER 1 | Out-Null

$copyDeadline = (Get-Date).AddSeconds($AssetCopyTimeoutSeconds)
do {
    Start-Sleep -Seconds 2
    $copyState = ((& $adb -s $Serial shell "if [ -s $externalFiles/melonloader/etc/managed/mscorlib.dll ] && [ -s $externalFiles/il2cpp/etc/mono/mconfig/config.xml ] && [ -s $externalFiles/Plugins/OtogiTranslate.dll ] && [ -s $externalFiles/Assets/font ] && [ -s $externalFiles/Assets/font.md5 ]; then echo ready; fi" 2>$null) -join "").Trim()
} until ($copyState -eq "ready" -or (Get-Date) -ge $copyDeadline)
if ($copyState -ne "ready") { throw "Timed out while LemonLoader copied runtime assets" }

$runtimeFontSha256 = (((& $adb -s $Serial shell "sha256sum $externalFiles/Assets/font" 2>$null) -join "") -split "\s+")[0].ToLowerInvariant()
$runtimePluginSha256 = (((& $adb -s $Serial shell "sha256sum $externalFiles/Plugins/OtogiTranslate.dll" 2>$null) -join "") -split "\s+")[0].ToLowerInvariant()
if ($runtimeFontSha256 -ne $expectedFontSha256) {
    throw "Replacement font mismatch: expected $expectedFontSha256, got $runtimeFontSha256"
}
if ($runtimePluginSha256 -ne $expectedPluginSha256) {
    throw "Plugin mismatch: expected $expectedPluginSha256, got $runtimePluginSha256"
}

& $adb -s $Serial shell am force-stop $PackageName
& $adb -s $Serial shell "rm -f $loaderLog"
& $adb -s $Serial logcat -c
& $adb -s $Serial shell monkey -p $PackageName -c android.intent.category.LAUNCHER 1 | Out-Null

$loaderOutput = ""
$loadDeadline = (Get-Date).AddSeconds($PluginLoadTimeoutSeconds)
do {
    Start-Sleep -Seconds 2
    $loaderOutput = (& $adb -s $Serial shell "cat $loaderLog" 2>$null) -join "`n"
} until (
    $loaderOutput.Contains("[OtogiTranslate] hook-failed") -or
    $loaderOutput.Contains("Failed to Invoke PreStart") -or
    $loaderOutput.Contains("[OtogiTranslate] tmp-scan-failed") -or
    ($loaderOutput.Contains("[OtogiTranslate] hook-installed") -and
        $loaderOutput.Contains("Assembly is up to date. No Generation Needed.") -and
        $loaderOutput.Contains("[OtogiTranslate] getter-hit") -and
        $loaderOutput.Contains("[OtogiTranslate] framerate-installed fps=60") -and
        $loaderOutput.Contains("[OtogiTranslate] mosaic-installed") -and
        $loaderOutput.Contains("[OtogiTranslate] font-redirect-installed") -and
        $loaderOutput.Contains("[OtogiTranslate] config-loaded")) -or
    (Get-Date) -ge $loadDeadline
)

$diagnostic = (& $adb -s $Serial logcat -d -v brief) |
    Select-String -Pattern "MelonLoader|libBootstrap|libmonosgen|Fatal signal|FATAL EXCEPTION|OtogiTranslate"
if (-not $loaderOutput.Contains("[OtogiTranslate] hook-installed") -or
    -not $loaderOutput.Contains("Assembly is up to date. No Generation Needed.") -or
    -not $loaderOutput.Contains("[OtogiTranslate] getter-hit") -or
    -not $loaderOutput.Contains("[OtogiTranslate] framerate-installed fps=60") -or
    -not $loaderOutput.Contains("[OtogiTranslate] mosaic-installed") -or
    -not $loaderOutput.Contains("[OtogiTranslate] font-redirect-installed") -or
    -not $loaderOutput.Contains("[OtogiTranslate] config-loaded") -or
    $loaderOutput.Contains("[OtogiTranslate] hook-failed") -or
    $loaderOutput.Contains("[OtogiTranslate] tmp-scan-failed") -or
    $loaderOutput.Contains("Failed to Invoke PreStart")) {
    $loaderOutput -split "`n" |
        Select-String -Pattern "MelonLoader v|OtogiTranslate|Failed to Invoke PreStart" |
        Select-Object -Last 40 | ForEach-Object { $_.Line }
    $diagnostic | Select-Object -Last 40 | ForEach-Object { $_.Line }
    throw "LemonLoader or OtogiTranslate runtime check failed"
}

$appPid = ((& $adb -s $Serial shell pidof $PackageName 2>$null) -join "").Trim()
if (-not $appPid) {
    $diagnostic | Select-Object -Last 80 | ForEach-Object { $_.Line }
    throw "App exited after LemonLoader initialization"
}
Write-Host "PASS LemonLoader, plugin hooks, font, and runtime assets; pid=$appPid"

[CmdletBinding()]
param(
    [string]$Serial = $(if ($env:ANDROID_SERIAL) { $env:ANDROID_SERIAL } else { "127.0.0.1:5555" }),
    [string]$AdbPath = $(if ($env:OTOGI_ADB) { $env:OTOGI_ADB } else { "adb" }),
    [string]$Apk = "out/Original.prototype-signed.apk",
    [string]$PackageName = "jp.co.dmm.dmmgames.kms.prototype",
    [int]$AssetCopyTimeoutSeconds = 180,
    [int]$PluginLoadTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($PackageName -cnotmatch '^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$') {
    throw "Invalid Android package name: $PackageName"
}

$adb = (Get-Command $AdbPath -ErrorAction Stop).Source
$script:externalFilesUseSu = $false

function Invoke-ExternalFileCommand([string]$Command) {
    $PSNativeCommandUseErrorActionPreference = $false
    # Keep all external-storage operations under the same verified shell identity.
    $quotedCommand = "'" + $Command.Replace("'", "'\''") + "'"
    $remoteCommand = if ($script:externalFilesUseSu) { "su -c $quotedCommand" } else { $Command }
    $output = & $adb -s $Serial shell $remoteCommand 2>&1
    if ($LASTEXITCODE -ne 0 -and -not $script:externalFilesUseSu) {
        $output = & $adb -s $Serial shell "su -c $quotedCommand" 2>&1
        if ($LASTEXITCODE -eq 0) {
            $script:externalFilesUseSu = $true
            Write-Host "External-file access requires su on this device"
        }
    }
    if ($LASTEXITCODE -ne 0) {
        throw "External-file command failed (shell/su access required): $Command`n$($output -join "`n")"
    }
    return ($output -join "`n").Trim()
}
$apkPath = if ([IO.Path]::IsPathRooted($Apk)) {
    $Apk
} else {
    Join-Path $PSScriptRoot $Apk
}
$pluginPath = Join-Path $PSScriptRoot "out/OtogiTranslate.dll"
$externalFiles = "/sdcard/Android/data/$PackageName/files"
$loaderLog = "$externalFiles/melonloader/etc/Latest.log"
$fontSource = "$externalFiles/UserData/OtogiTranslate/font"
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
$suIdentity = ((& $adb -s $Serial shell "su -c 'id -u'" 2>$null) -join "").Trim()
$script:externalFilesUseSu = $LASTEXITCODE -eq 0 -and $suIdentity -eq "0"

& $adb -s $Serial install -r $apkPath
if ($LASTEXITCODE -ne 0) { throw "APK install failed" }

# Refresh build-owned files without deleting login, config, or translation cache.
& $adb -s $Serial shell am force-stop $PackageName
Invoke-ExternalFileCommand "rm -f $externalFiles/Plugins/OtogiTranslate.dll $externalFiles/Plugins/OtogiCgUnlock.dll $fontSource $externalFiles/Assets/font $externalFiles/Assets/font.md5 $loaderLog" | Out-Null
& $adb -s $Serial shell monkey -p $PackageName -c android.intent.category.LAUNCHER 1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "App launch failed" }

$copyDeadline = (Get-Date).AddSeconds($AssetCopyTimeoutSeconds)
do {
    Start-Sleep -Seconds 2
    $copyState = Invoke-ExternalFileCommand "ls -A $externalFiles >/dev/null && if [ -s $externalFiles/melonloader/etc/managed/mscorlib.dll ] && [ -s $externalFiles/il2cpp/etc/mono/mconfig/config.xml ] && [ -s $externalFiles/Plugins/OtogiTranslate.dll ] && [ -s $fontSource ]; then echo ready; fi"
    $appPid = ((& $adb -s $Serial shell pidof $PackageName 2>$null) -join "").Trim()
    if (-not $appPid) {
        throw "App exited during first-launch asset initialization; inspect bootstrap logcat/tombstone before retrying"
    }
} until ($copyState -eq "ready" -or (Get-Date) -ge $copyDeadline)
if ($copyState -ne "ready") { throw "Timed out while LemonLoader copied runtime assets" }

$runtimeFontSha256 = ((Invoke-ExternalFileCommand "sha256sum $fontSource") -split "\s+")[0].ToLowerInvariant()
$runtimePluginSha256 = ((Invoke-ExternalFileCommand "sha256sum $externalFiles/Plugins/OtogiTranslate.dll") -split "\s+")[0].ToLowerInvariant()
if ($runtimeFontSha256 -ne $expectedFontSha256) {
    throw "Replacement font mismatch: expected $expectedFontSha256, got $runtimeFontSha256"
}
if ($runtimePluginSha256 -ne $expectedPluginSha256) {
    throw "Plugin mismatch: expected $expectedPluginSha256, got $runtimePluginSha256"
}

& $adb -s $Serial shell am force-stop $PackageName
Invoke-ExternalFileCommand "rm -f $loaderLog" | Out-Null
& $adb -s $Serial logcat -c
& $adb -s $Serial shell monkey -p $PackageName -c android.intent.category.LAUNCHER 1 | Out-Null

$loaderOutput = ""
$loadDeadline = (Get-Date).AddSeconds($PluginLoadTimeoutSeconds)
do {
    Start-Sleep -Seconds 2
    $loaderOutput = Invoke-ExternalFileCommand "ls -A $externalFiles >/dev/null && if [ -e $loaderLog ]; then cat $loaderLog; fi"
} until (
    $loaderOutput.Contains("[OtogiTranslate] hook-failed") -or
    $loaderOutput.Contains("[OtogiCgUnlock] hook-failed") -or
    $loaderOutput.Contains("Failed to Invoke PreStart") -or
    $loaderOutput.Contains("[OtogiTranslate] runtime-init-failed") -or
    ($loaderOutput.Contains("[OtogiTranslate] hook-installed") -and
        $loaderOutput.Contains("[OtogiCgUnlock] installed") -and
        $loaderOutput.Contains("Assembly is up to date. No Generation Needed.") -and
        $loaderOutput.Contains("[OtogiTranslate] getter-hit") -and
        $loaderOutput.Contains("[OtogiTranslate] framerate-installed fps=60") -and
        $loaderOutput.Contains("[OtogiTranslate] mosaic-installed") -and
        $loaderOutput.Contains("[OtogiTranslate] font-redirect-installed") -and
        $loaderOutput.Contains("[OtogiTranslate] config-loaded") -and
        $loaderOutput.Contains("[OtogiTranslate] dictionary-transport-installed") -and
        $loaderOutput.Contains("[OtogiTranslate] runtime-driver-installed")) -or
    (Get-Date) -ge $loadDeadline
)

$diagnostic = (& $adb -s $Serial logcat -d -v brief) |
    Select-String -Pattern "MelonLoader|libBootstrap|libmonosgen|Fatal signal|FATAL EXCEPTION|OtogiTranslate|OtogiCgUnlock"
if (-not $loaderOutput.Contains("[OtogiTranslate] hook-installed") -or
    -not $loaderOutput.Contains("[OtogiCgUnlock] installed") -or
    -not $loaderOutput.Contains("Assembly is up to date. No Generation Needed.") -or
    -not $loaderOutput.Contains("[OtogiTranslate] getter-hit") -or
    -not $loaderOutput.Contains("[OtogiTranslate] framerate-installed fps=60") -or
    -not $loaderOutput.Contains("[OtogiTranslate] mosaic-installed") -or
    -not $loaderOutput.Contains("[OtogiTranslate] font-redirect-installed") -or
    -not $loaderOutput.Contains("[OtogiTranslate] config-loaded") -or
    -not $loaderOutput.Contains("[OtogiTranslate] dictionary-transport-installed") -or
    -not $loaderOutput.Contains("[OtogiTranslate] runtime-driver-installed") -or
    $loaderOutput.Contains("[OtogiTranslate] hook-failed") -or
    $loaderOutput.Contains("[OtogiCgUnlock] hook-failed") -or
    $loaderOutput.Contains("[OtogiTranslate] runtime-init-failed") -or
    $loaderOutput.Contains("Failed to Invoke PreStart")) {
    $loaderOutput -split "`n" |
        Select-String -Pattern "MelonLoader v|OtogiTranslate|OtogiCgUnlock|Failed to Invoke PreStart" |
        Select-Object -Last 40 | ForEach-Object { $_.Line }
    $diagnostic | Select-Object -Last 40 | ForEach-Object { $_.Line }
    throw "LemonLoader or OtogiTranslate runtime check failed"
}

$appPid = ((& $adb -s $Serial shell pidof $PackageName 2>$null) -join "").Trim()
if (-not $appPid) {
    $diagnostic | Select-Object -Last 80 | ForEach-Object { $_.Line }
    throw "App exited after LemonLoader initialization"
}
Write-Host "PASS LemonLoader, production plugin hooks, font, and runtime assets; pid=$appPid"

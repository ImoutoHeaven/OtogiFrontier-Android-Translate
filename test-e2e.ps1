[CmdletBinding()]
param(
    [string]$Serial = $(if ($env:ANDROID_SERIAL) { $env:ANDROID_SERIAL } else { "127.0.0.1:5555" }),
    [string]$AdbPath = $(if ($env:OTOGI_ADB) { $env:OTOGI_ADB } else { "adb" }),
    [string]$PackageName = "jp.co.dmm.dmmgames.kms.prototype",
    [Parameter(Mandatory)]
    [string]$ExpectedEndpoint,
    [Parameter(Mandatory)]
    [string]$ExpectedModel,
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$adb = (Get-Command $AdbPath -ErrorAction Stop).Source
if ($PackageName -notmatch '^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$') {
    throw "Invalid Android package name"
}
if ($TimeoutSeconds -lt 1) { throw "TimeoutSeconds must be positive" }
$pluginPath = Join-Path $PSScriptRoot "out/OtogiTranslate.dll"
if (-not (Test-Path -LiteralPath $pluginPath -PathType Leaf)) {
    throw "Plugin not found: $pluginPath"
}
$expectedPluginSha256 = (Get-FileHash -LiteralPath $pluginPath -Algorithm SHA256).Hash.ToLowerInvariant()
$files = "/sdcard/Android/data/$PackageName/files"
$log = "$files/melonloader/etc/Latest.log"
$translations = "$files/UserData/OtogiTranslate"
$backup = "$files/.otogi-translate-e2e-backup"
$run = Get-Date -Format "yyyyMMdd-HHmmss"
$evidence = Join-Path $PSScriptRoot "e2e/$run"
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

function Invoke-AdbShell([string]$Command) {
    $output = & $adb -s $Serial shell $Command
    if ($LASTEXITCODE -ne 0) { throw "adb shell failed" }
    return ($output -join "`n")
}

function Invoke-AdbCommand([string[]]$Arguments) {
    $output = & $adb -s $Serial @Arguments
    if ($LASTEXITCODE -ne 0) { throw "adb command failed" }
    return ($output -join "`n")
}

function Read-IniSection([string]$Text, [string]$Name) {
    $values = @{}
    $section = ""
    foreach ($rawLine in ($Text -split "`r?`n")) {
        $line = $rawLine.Trim()
        if (-not $line -or $line[0] -in @("#", ";")) { continue }
        if ($line -match '^\[(.*)\]$') {
            $section = $Matches[1].Trim()
            continue
        }
        if ($section -ine $Name) { continue }
        $equals = $line.IndexOf("=")
        if ($equals -le 0) { continue }
        $key = $line.Substring(0, $equals).Trim().ToLowerInvariant()
        $values[$key] = $line.Substring($equals + 1).Trim()
    }
    return $values
}

function Read-LoaderLog {
    return ((& $adb -s $Serial shell "cat '$log'" 2>$null) -join "`n")
}

function Wait-LoaderLog([string[]]$Needles, [int]$Seconds = $TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        Start-Sleep -Seconds 2
        $text = Read-LoaderLog
        $missing = @($Needles | Where-Object { -not $text.Contains($_) })
    } until ($missing.Count -eq 0 -or (Get-Date) -ge $deadline)
    if ($missing.Count -ne 0) {
        Save-Screenshot "failure"
        throw "Timed out waiting for: $($missing -join ', ')"
    }
    return $text
}

function Save-Screenshot([string]$Name) {
    $path = Join-Path $evidence "$Name.png"
    $process = Start-Process -FilePath $adb -ArgumentList @(
        "-s", $Serial, "exec-out", "screencap", "-p"
    ) -RedirectStandardOutput $path -NoNewWindow -Wait -PassThru
    if ($process.ExitCode -ne 0 -or (Get-Item $path).Length -eq 0) {
        throw "Screenshot failed: $Name"
    }
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 24) { throw "Invalid screenshot: $Name" }
    $width = ([int]$bytes[16] -shl 24) -bor ([int]$bytes[17] -shl 16) -bor
        ([int]$bytes[18] -shl 8) -bor [int]$bytes[19]
    $height = ([int]$bytes[20] -shl 24) -bor ([int]$bytes[21] -shl 16) -bor
        ([int]$bytes[22] -shl 8) -bor [int]$bytes[23]
    if ($width -ne 1920 -or $height -ne 1080) {
        throw "E2E requires a 1920x1080 landscape game surface; got ${width}x${height}"
    }
}

function Tap([int]$X, [int]$Y) {
    Invoke-AdbShell "input tap $X $Y" | Out-Null
}

function Measure-FrameRate {
    Invoke-AdbShell "dumpsys SurfaceFlinger --timestats -clear" | Out-Null
    Invoke-AdbShell "dumpsys SurfaceFlinger --timestats -enable" | Out-Null
    try {
        Start-Sleep -Seconds 10
        $stats = Invoke-AdbShell "dumpsys SurfaceFlinger --timestats -dump"
    }
    finally {
        Invoke-AdbShell "dumpsys SurfaceFlinger --timestats -disable" | Out-Null
    }
    Set-Content -LiteralPath (Join-Path $evidence "surfaceflinger-timestats.txt") -Encoding utf8 -Value $stats
    $packagePattern = [Regex]::Escape($PackageName)
    $match = [Regex]::Match($stats,
        "(?ms)displayRefreshRate = 60 fps\r?\nrenderRate = 60 fps\r?\n.*?layerName = [^\r\n]*$packagePattern[^\r\n]*\r?\n.*?totalFrames = (\d+).*?averageFPS = ([0-9.]+)")
    if (-not $match.Success) { throw "No SurfaceFlinger frame data for the game layer" }
    $frames = [int]$match.Groups[1].Value
    $fps = [double]::Parse($match.Groups[2].Value,
        [Globalization.CultureInfo]::InvariantCulture)
    if ($frames -lt 500 -or $fps -lt 58 -or $fps -gt 70) {
        throw "Frame-rate check failed: frames=$frames fps=$fps"
    }
}

function Assert-CleanLog([string]$Text) {
    $failures = @(@(
        "[OtogiTranslate] hook-failed",
        "[OtogiTranslate] runtime-init-failed",
        "[OtogiTranslate] framerate-failed",
        "[OtogiTranslate] mosaic-failed",
        "[OtogiTranslate] mosaic-error",
        "[OtogiTranslate] font-redirect-failed",
        "[OtogiTranslate] font-redirect-error",
        "[OtogiTranslate] font-local-error",
        "[OtogiTranslate] response-error",
        "[OtogiTranslate] dictionary-prefetch-error",
        "[OtogiTranslate] dictionary-download-failed",
        "[OtogiTranslate] dictionary-quarantine-failed",
        "[OtogiTranslate] adult-dictionary-mapping-missing",
        "[OtogiTranslate] ui-scan-error",
        "[OtogiTranslate] llm-error"
    ) | Where-Object { $Text.Contains($_) })
    if ($failures.Count -ne 0) {
        throw "Plugin error marker found: $($failures -join ', ')"
    }
}

& $adb connect $Serial | Out-Null
if ($LASTEXITCODE -ne 0) { throw "ADB connect failed: $Serial" }
if ((Invoke-AdbCommand -Arguments @("get-state")).Trim() -ne "device") {
    throw "ADB device is not ready: $Serial"
}

$runtimePluginSha256 = ((Invoke-AdbShell "sha256sum '$files/Plugins/OtogiTranslate.dll'") -split "\s+")[0].ToLowerInvariant()
if ($runtimePluginSha256 -ne $expectedPluginSha256) {
    throw "Installed plugin does not match the local production DLL"
}
$config = Invoke-AdbShell "cat '$files/OtogiTranslate.cfg'"
$llmConfig = Read-IniSection $config "LLM"
$enabled = ([string]$llmConfig["enable"]).ToLowerInvariant()
if (@("true", "1", "yes", "on") -notcontains $enabled -or
    [string]$llmConfig["endpoint"] -cne $ExpectedEndpoint -or
    [string]$llmConfig["model"] -cne $ExpectedModel -or
    [string]::IsNullOrWhiteSpace([string]$llmConfig["apikey"])) {
    throw "[LLM] must use the expected endpoint/model with Enable=true and a non-empty ApiKey"
}
Set-Content -LiteralPath (Join-Path $evidence "environment.txt") -Encoding utf8 -Value @(
    "package=$PackageName",
    "llmConfiguration=validated",
    "pluginSha256=$runtimePluginSha256"
)

$fontSha256 = "929faeecb6a0bd636b92a921d2d590350b72e301832272302a064f3d5a0ab893"
if ((Invoke-AdbShell "[ -e '$backup' ] && echo yes || true").Trim() -eq "yes") {
    throw "Stale E2E backup exists on the device: $backup"
}
Invoke-AdbShell "am force-stop '$PackageName'" | Out-Null
$hadCache = (Invoke-AdbShell "[ -f '$files/OtogiTranslate.cache.jsonl' ] && echo yes || true").Trim() -eq "yes"
$hadTranslations = (Invoke-AdbShell "[ -d '$translations' ] && echo yes || true").Trim() -eq "yes"
$passed = $false

try {
    $prepare = "set -e; mkdir -p '$backup'; " +
        "[ ! -f '$files/OtogiTranslate.cache.jsonl' ] || mv '$files/OtogiTranslate.cache.jsonl' '$backup/cache.jsonl'; " +
        $(if ($hadTranslations) { "cp -a '$translations' '$backup/dictionaries'; touch '$backup/dictionaries.complete'; " } else { "" }) +
        "mkdir -p '$translations/MScenes' '$translations/MAdults'; " +
        "rm -f '$translations/MScenes/10001_gb.json' '$translations/MAdults/10001_gb.json' '$log'"
    Invoke-AdbShell $prepare | Out-Null
    Invoke-AdbCommand -Arguments @("logcat", "-c") | Out-Null
    Invoke-AdbCommand -Arguments @(
        "shell", "monkey -p '$PackageName' -c android.intent.category.LAUNCHER 1"
    ) | Out-Null

    $runtime = Wait-LoaderLog @(
        "[OtogiTranslate] hook-installed",
        "[OtogiTranslate] framerate-installed fps=60",
        "[OtogiTranslate] mosaic-installed",
        "[OtogiTranslate] font-redirect-installed",
        "[OtogiTranslate] dictionary-transport-installed UnityWebRequest",
        "[OtogiTranslate] runtime-driver-installed EventSystem.Update",
        "[OtogiTranslate] llm-transport-installed UnityWebRequest",
        "[OtogiTranslate] ui-scan-installed",
        "[OtogiTranslate] getter-hit"
    )
    Assert-CleanLog $runtime

    $llm = Wait-LoaderLog @(
        "[OtogiTranslate] llm-request-started transport=UnityWebRequest",
        "[OtogiTranslate] llm-translated",
        "[OtogiTranslate] ui-translation-applied"
    )
    Assert-CleanLog $llm
    Save-Screenshot "llm-ui"

    # The warning page precedes the title page on a cold launch.
    Start-Sleep -Seconds 15
    Tap 960 540
    Start-Sleep -Seconds 35
    Save-Screenshot "translated-title"
    Tap 960 540
    Start-Sleep -Seconds 35
    Save-Screenshot "home"
    Measure-FrameRate

    Tap 1690 350
    Start-Sleep -Seconds 8
    Save-Screenshot "character-story"
    Tap 170 310

    $prefetch = Wait-LoaderLog @(
        "[OtogiTranslate] dictionary-downloaded type=MScenes id=10001",
        "[OtogiTranslate] dictionary-downloaded type=MAdults id=10001"
    )
    Assert-CleanLog $prefetch
    Save-Screenshot "episode-list"

    Tap 1700 245
    Start-Sleep -Seconds 2
    Save-Screenshot "episode-choice"
    Tap 675 640
    $scene = Wait-LoaderLog @(
        "[OtogiTranslate] translated type=MScenes id=10001"
    )
    Assert-CleanLog $scene
    $sceneResult = [Regex]::Match($scene,
        '\[OtogiTranslate\] translated type=MScenes id=10001 dictionary=10001 replacements=(\d+)')
    if (-not $sceneResult.Success -or [int]$sceneResult.Groups[1].Value -lt 100) {
        throw "Ordinary-scene dictionary did not replace substantive dialogue"
    }
    Start-Sleep -Seconds 3
    Save-Screenshot "translated-scene"

    Tap 130 1015
    $adult = Wait-LoaderLog @(
        "[OtogiTranslate] translated type=MAdults id=210011",
        "[OtogiTranslate] mosaic-patched block-size=0.001"
    )
    Assert-CleanLog $adult
    $adultResult = [Regex]::Match($adult,
        '\[OtogiTranslate\] translated type=MAdults id=210011 dictionary=10001 replacements=(\d+)')
    if (-not $adultResult.Success -or [int]$adultResult.Groups[1].Value -lt 60) {
        throw "Adult-scene dictionary did not replace substantive dialogue"
    }
    Start-Sleep -Seconds 3
    Save-Screenshot "translated-adult"

    $runtimeFont = ((Invoke-AdbShell "sha256sum '$files/Assets/font'") -split "\s+")[0].ToLowerInvariant()
    if ($runtimeFont -ne $fontSha256) {
        throw "Replacement font mismatch: $runtimeFont"
    }
    if (-not (Invoke-AdbShell "pidof '$PackageName'").Trim()) {
        throw "Game process exited during E2E"
    }

    Invoke-AdbCommand -Arguments @(
        "pull", $log, (Join-Path $evidence "Latest.log")
    ) | Out-Null
    $passed = $true
}
finally {
    $restore = "set -e; am force-stop '$PackageName'; " +
        $(if ($hadCache) { "[ ! -f '$backup/cache.jsonl' ] || mv -f '$backup/cache.jsonl' '$files/OtogiTranslate.cache.jsonl'; " } else { "rm -f '$files/OtogiTranslate.cache.jsonl'; " }) +
        $(if ($hadTranslations) { "if [ -f '$backup/dictionaries.complete' ]; then mv '$translations' '$backup/generated-dictionaries'; mv '$backup/dictionaries' '$translations'; rm -rf '$backup/generated-dictionaries'; rm '$backup/dictionaries.complete'; fi; " } else { "rm -rf '$translations'; " }) +
        "[ ! -d '$backup' ] || rmdir '$backup'"
    Invoke-AdbShell $restore | Out-Null
}

if ($passed) {
    Set-Content -LiteralPath (Join-Path $evidence "PASS.txt") -Encoding utf8 -Value @(
        "PASS production plugin hash",
        "PASS SurfaceFlinger game-layer frame rate >=58 FPS",
        "PASS replacement font hash",
        "PASS live LLM request and UI text application",
        "PASS remote dictionary prefetch and substantive ordinary/adult response translation",
        "PASS live Spine mosaic material patch"
    )
    Write-Host "PASS real game E2E; evidence=$evidence"
}

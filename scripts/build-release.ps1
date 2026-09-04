param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$AppOutput = Join-Path $ProjectRoot "outputs\FIFINE-SC3-RGB-Plus-v2.3.0-beta"
$UpdaterOutput = Join-Path $ProjectRoot "outputs\SC3FirmwareTool-beta"
$InstallerScript = Join-Path $ProjectRoot "Installer\FIFINE-SC3-RGB-Plus.iss"

$Firmware = Join-Path $ProjectRoot "firmware\candidates\mod14\SC3-V22-RGB-Mod-1.4-Attestation-Candidate.mva"
$ExpectedFirmwareHash = "FB763B1F4E318B529F932897B63B723545F75F090FC220D9DD666198E73955B8"

if (-not (Test-Path $Firmware)) {
    throw "Validated Mod 1.4 firmware is missing."
}

$FirmwareHash = (Get-FileHash $Firmware -Algorithm SHA256).Hash
if ($FirmwareHash -ne $ExpectedFirmwareHash) {
    throw "Validated Mod 1.4 firmware SHA-256 mismatch."
}

dotnet publish (Join-Path $ProjectRoot "SC3RGBController.csproj") `
    -c $Configuration -r win-x64 --self-contained true -o $AppOutput

dotnet publish (Join-Path $ProjectRoot "Updater\SC3FirmwareTool\SC3FirmwareTool.csproj") `
    -c $Configuration -r win-x64 --self-contained true -o $UpdaterOutput

$InnoCandidates = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) }

$ISCC = $InnoCandidates | Select-Object -First 1
if (-not $ISCC) {
    throw "Inno Setup 6 compiler (ISCC.exe) was not found."
}

Push-Location (Split-Path -Parent $InstallerScript)
try {
    & $ISCC (Split-Path -Leaf $InstallerScript)
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compiler failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$Installer = Join-Path $ProjectRoot "outputs\installer\FIFINE-SC3-RGB-Plus-2.3.0-beta-Setup.exe"
if (-not (Test-Path $Installer)) {
    throw "Installer build did not produce the expected Setup.exe."
}

Write-Output "Release build: PASS"
Write-Output "Installer build: PASS"
Write-Output "Firmware SHA-256: $FirmwareHash"
Write-Output "Installer SHA-256: $((Get-FileHash $Installer -Algorithm SHA256).Hash)"

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$VersionPropsPath = Join-Path $ProjectRoot "Version.props"
[xml]$VersionProps = Get-Content $VersionPropsPath
$VersionGroup = $VersionProps.Project.PropertyGroup
$AppSemanticVersion = [string]$VersionGroup.AppSemanticVersion
$AppNumericVersion = [string]$VersionGroup.FileVersion
if ([string]::IsNullOrWhiteSpace($AppSemanticVersion) -or [string]::IsNullOrWhiteSpace($AppNumericVersion)) {
    throw "Version.props is missing AppSemanticVersion or FileVersion."
}

$AppOutput = Join-Path $ProjectRoot "outputs\FIFINE-SC3-RGB-Plus-v$AppSemanticVersion"
$UpdaterOutput = Join-Path $ProjectRoot "outputs\SC3FirmwareTool-v$AppSemanticVersion"
$InstallerScript = Join-Path $ProjectRoot "Installer\FIFINE-SC3-RGB-Plus.iss"

$Firmware = Join-Path $ProjectRoot "firmware\candidates\mod14\SC3-V22-RGB-Mod-1.4-Attestation-Candidate.mva"
$ExpectedFirmwareHash = "FB763B1F4E318B529F932897B63B723545F75F090FC220D9DD666198E73955B8"
$StockFirmware = Join-Path $ProjectRoot "firmware\recovery\SC3_V22_recovery.MVA"
$ExpectedStockFirmwareHash = "01A282431C3D82FFD64AA7095F8E151893F459094E2C5EE08010DBA430CFFCDD"

if (-not (Test-Path $Firmware)) {
    throw "Validated Mod 1.4 firmware is missing."
}

$FirmwareHash = (Get-FileHash $Firmware -Algorithm SHA256).Hash
if ($FirmwareHash -ne $ExpectedFirmwareHash) {
    throw "Validated Mod 1.4 firmware SHA-256 mismatch."
}

if (-not (Test-Path $StockFirmware)) {
    throw "Validated Stock V22 recovery firmware is missing."
}

$StockFirmwareHash = (Get-FileHash $StockFirmware -Algorithm SHA256).Hash
if ($StockFirmwareHash -ne $ExpectedStockFirmwareHash) {
    throw "Validated Stock V22 recovery firmware SHA-256 mismatch."
}

dotnet publish (Join-Path $ProjectRoot "SC3RGBController.csproj") `
    -c $Configuration -r win-x64 --self-contained true -o $AppOutput
if ($LASTEXITCODE -ne 0) {
    throw "Application publish failed with exit code $LASTEXITCODE."
}

dotnet publish (Join-Path $ProjectRoot "Updater\SC3FirmwareTool\SC3FirmwareTool.csproj") `
    -c $Configuration -r win-x64 --self-contained true -o $UpdaterOutput
if ($LASTEXITCODE -ne 0) {
    throw "Native updater publish failed with exit code $LASTEXITCODE."
}

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
    $VersionDefine = "/DMyAppVersion=`"$AppSemanticVersion`""
    $NumericVersionDefine = "/DMyAppNumericVersion=`"$AppNumericVersion`""
    $UpdaterOutputDefine = "/DUpdaterOutputFolder=`"SC3FirmwareTool-v$AppSemanticVersion`""
    & $ISCC $VersionDefine $NumericVersionDefine $UpdaterOutputDefine (Split-Path -Leaf $InstallerScript)
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compiler failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$Installer = Join-Path $ProjectRoot "outputs\installer\FIFINE-SC3-RGB-Plus-$AppSemanticVersion-Setup.exe"
if (-not (Test-Path $Installer)) {
    throw "Installer build did not produce the expected Setup.exe."
}

$InstallerHash = (Get-FileHash $Installer -Algorithm SHA256).Hash
$ManifestPath = Join-Path $ProjectRoot "outputs\installer\update-manifest.json"
$ChecksumsPath = Join-Path $ProjectRoot "outputs\installer\SHA256SUMS.txt"
$Manifest = [ordered]@{
    version = $AppSemanticVersion
    installer = [System.IO.Path]::GetFileName($Installer)
    sha256 = $InstallerHash
}
$Manifest | ConvertTo-Json | Set-Content -Path $ManifestPath -Encoding utf8
"$InstallerHash  $([System.IO.Path]::GetFileName($Installer))" | Set-Content -Path $ChecksumsPath -Encoding ascii

Write-Output "Release build: PASS"
Write-Output "Installer build: PASS"
Write-Output "Application version: $AppSemanticVersion"
Write-Output "Firmware SHA-256: $FirmwareHash"
Write-Output "Stock recovery SHA-256: $StockFirmwareHash"
Write-Output "Installer SHA-256: $InstallerHash"
Write-Output "Update manifest: $ManifestPath"
Write-Output "Checksums: $ChecksumsPath"

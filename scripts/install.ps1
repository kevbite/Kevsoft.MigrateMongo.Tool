# Installs the migrate-mongo Native AOT binary on Windows. No .NET needed.
#
#   irm https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/releases/latest/download/install.ps1 | iex
#
# Environment variables (or parameters when the script is run as a file):
#   MIGRATE_MONGO_VERSION        release to install, e.g. 0.2.0 (default: latest)
#   MIGRATE_MONGO_INSTALL_DIR    default: %LOCALAPPDATA%\Programs\migrate-mongo
#   MIGRATE_MONGO_DOWNLOAD_URL   base URL (or folder) of the release assets, for mirrors and testing
#   MIGRATE_MONGO_NO_PATH        set to 1 to leave the user PATH alone
param(
    [string]$Version = $env:MIGRATE_MONGO_VERSION,
    [string]$InstallDir = $env:MIGRATE_MONGO_INSTALL_DIR,
    [string]$DownloadUrl = $env:MIGRATE_MONGO_DOWNLOAD_URL,
    [switch]$NoPathUpdate = ($env:MIGRATE_MONGO_NO_PATH -eq '1')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$repository = 'kevbite/Kevsoft.MigrateMongo.Tool'
if ($Version) { $Version = $Version.TrimStart('v') }
if (-not $DownloadUrl) {
    $DownloadUrl = if (-not $Version -or $Version -eq 'latest') {
        "https://github.com/$repository/releases/latest/download"
    } else {
        "https://github.com/$repository/releases/download/v$Version"
    }
}
$DownloadUrl = $DownloadUrl.TrimEnd('/', '\')

$architecture = try { [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() } catch { $env:PROCESSOR_ARCHITECTURE }
$arch = switch -Regex ($architecture) {
    '^(X64|AMD64)$' { 'x64' }
    '^(Arm64|ARM64)$' { 'arm64' }
    default { throw "migrate-mongo install: $architecture isn't supported. Install the .NET tool instead: dotnet tool install -g Kevsoft.MigrateMongo.Tool" }
}

$asset = "migrate-mongo-win-$arch.zip"
if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\migrate-mongo' }

function Get-Asset([string]$Name, [string]$Destination) {
    $source = "$DownloadUrl/$Name"
    if ($source -match '^https?://') {
        Invoke-WebRequest -Uri $source -OutFile $Destination -UseBasicParsing
    } else {
        Copy-Item -LiteralPath (($source -replace '^file:///?', '') -replace '/', '\') -Destination $Destination
    }
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ("migrate-mongo-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    Write-Host "Downloading $DownloadUrl/$asset"
    Get-Asset $asset (Join-Path $temp $asset)
    Get-Asset 'SHA256SUMS' (Join-Path $temp 'SHA256SUMS')

    $expected = Get-Content (Join-Path $temp 'SHA256SUMS') |
        ForEach-Object { $parts = $_ -split '\s+', 2; if ($parts.Count -eq 2 -and $parts[1].TrimStart('*') -eq $asset) { $parts[0] } } |
        Select-Object -First 1
    if (-not $expected) { throw "migrate-mongo install: $asset isn't listed in SHA256SUMS" }
    $actual = (Get-FileHash -Algorithm SHA256 (Join-Path $temp $asset)).Hash
    if ($actual -ne $expected) { throw "migrate-mongo install: checksum mismatch for $asset (expected $expected, got $actual)" }

    Expand-Archive -LiteralPath (Join-Path $temp $asset) -DestinationPath (Join-Path $temp 'out')
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    $exe = Join-Path $InstallDir 'migrate-mongo.exe'
    # A running copy can't be overwritten but it can be renamed out of the way.
    if (Test-Path $exe) { Move-Item -LiteralPath $exe -Destination "$exe.old" -Force }
    Copy-Item -LiteralPath (Join-Path $temp 'out\migrate-mongo.exe') -Destination $exe
    Remove-Item -LiteralPath "$exe.old" -Force -ErrorAction SilentlyContinue
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Installed $(& $exe version) to $exe"

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$onPath = @($userPath -split ';') -contains $InstallDir
if (-not $onPath -and -not $NoPathUpdate) {
    [Environment]::SetEnvironmentVariable('Path', ((@($userPath -split ';' | Where-Object { $_ }) + $InstallDir) -join ';'), 'User')
    $env:Path = "$env:Path;$InstallDir"
    Write-Host "Added $InstallDir to your user PATH. Open a new terminal to use migrate-mongo."
} elseif (-not $onPath) {
    Write-Host "Add $InstallDir to your PATH to run migrate-mongo."
}

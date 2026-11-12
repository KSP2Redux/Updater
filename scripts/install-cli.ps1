<#
.SYNOPSIS
    Installs, updates or removes the KSP2 Redux launcher CLI.

.DESCRIPTION
    Downloads the newest redux-launcher-cli release from GitHub, checks it against the SHA256 the
    releases API publishes for it, and puts it in a per-user folder that is added to PATH. Nothing
    here needs administrator rights and nothing is written outside that folder and the user PATH.

    Re-running upgrades in place. The launcher config and logs are shared with the launcher window
    and are never touched, including by -Uninstall.

.PARAMETER Version
    Install this exact version instead of the newest one, for example 0.4.2.3.

.PARAMETER InstallDirectory
    Where to put the binary. Defaults to %LOCALAPPDATA%\Programs\redux-launcher-cli.

.PARAMETER Uninstall
    Remove the binary and take its folder off PATH.

.EXAMPLE
    irm https://raw.githubusercontent.com/KSP2Redux/Updater/main/scripts/install-cli.ps1 | iex

.EXAMPLE
    ./install-cli.ps1 -Version 0.4.2.3
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\redux-launcher-cli'),
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'

$repository = 'KSP2Redux/Updater'
$assetName = 'redux-cli-x64.exe'
$executableName = 'redux-launcher-cli.exe'
$tagPrefix = 'updater-v'

function Get-UserPathEntries {
    $path = [Environment]::GetEnvironmentVariable('PATH', 'User')
    if ([string]::IsNullOrWhiteSpace($path)) { return @() }
    return $path.Split(';', [StringSplitOptions]::RemoveEmptyEntries)
}

function Add-ToUserPath([string] $directory) {
    $entries = Get-UserPathEntries
    if ($entries | Where-Object { $_.TrimEnd('\') -ieq $directory.TrimEnd('\') }) {
        return $false
    }

    [Environment]::SetEnvironmentVariable('PATH', (@($entries) + $directory) -join ';', 'User')
    return $true
}

function Remove-FromUserPath([string] $directory) {
    $entries = Get-UserPathEntries
    $kept = $entries | Where-Object { $_.TrimEnd('\') -ine $directory.TrimEnd('\') }
    if ($kept.Count -eq $entries.Count) { return $false }

    [Environment]::SetEnvironmentVariable('PATH', ($kept -join ';'), 'User')
    return $true
}

if ($Uninstall) {
    $target = Join-Path $InstallDirectory $executableName
    if (Test-Path $target) {
        Remove-Item $target -Force
        Write-Host "Removed $target"
    }
    else {
        Write-Host "Nothing to remove at $target"
    }

    Remove-Item (Join-Path $InstallDirectory "$executableName.old") -Force -ErrorAction SilentlyContinue

    if (Remove-FromUserPath $InstallDirectory) {
        Write-Host "Took $InstallDirectory off your PATH. Open a new terminal for that to apply."
    }

    if ((Test-Path $InstallDirectory) -and -not (Get-ChildItem $InstallDirectory -Force)) {
        Remove-Item $InstallDirectory -Force
    }

    Write-Host 'Your launcher config and logs were left alone.'
    return
}

Write-Host 'Looking for the newest redux-launcher-cli release...'

$releases = Invoke-RestMethod -Uri "https://api.github.com/repos/$repository/releases" -Headers @{
    'User-Agent' = 'install-cli.ps1'
    'Accept'     = 'application/vnd.github+json'
}

# The CLI ships inside the launcher's release, so these are the same tags the launcher uses.
# Releases from before the CLI existed simply have no matching asset and are skipped below.
$candidates = $releases |
    Where-Object { $_.tag_name -like "$tagPrefix*" -and -not $_.prerelease -and -not $_.draft } |
    ForEach-Object {
        $parsed = $null
        if ([Version]::TryParse($_.tag_name.Substring($tagPrefix.Length), [ref] $parsed)) {
            [pscustomobject]@{ Release = $_; Version = $parsed }
        }
    }

if ($Version) {
    $candidates = $candidates | Where-Object { $_.Version -eq [Version] $Version }
}

$selected = $candidates | Sort-Object Version -Descending | Select-Object -First 1
if (-not $selected) {
    throw "No $tagPrefix release was found in $repository$(if ($Version) { " for version $Version" })."
}

$asset = $selected.Release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
if (-not $asset) {
    throw "Release $($selected.Release.tag_name) has no $assetName. It may still be uploading."
}

Write-Host "Downloading $assetName from $($selected.Release.tag_name)..."

$temporary = Join-Path ([IO.Path]::GetTempPath()) "$assetName.$([Guid]::NewGuid().ToString('n'))"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $temporary -UseBasicParsing

try {
    # The releases API publishes the digest it computed on upload, so a truncated or tampered
    # download is caught here rather than the first time someone runs the binary.
    if ($asset.digest -and $asset.digest.StartsWith('sha256:')) {
        $expected = $asset.digest.Substring('sha256:'.Length).Trim()
        $actual = (Get-FileHash -Path $temporary -Algorithm SHA256).Hash
        if ($actual -ine $expected) {
            throw "Checksum mismatch for $assetName. Expected $expected, got $actual."
        }
        Write-Host 'Checksum verified.'
    }
    else {
        Write-Warning "Release $($selected.Release.tag_name) published no checksum for $assetName, skipping verification."
    }

    New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
    $target = Join-Path $InstallDirectory $executableName

    # Windows will not overwrite a running executable but it will rename one, so an upgrade run from
    # a shell that still has the old CLI open keeps working.
    if (Test-Path $target) {
        $superseded = "$target.old"
        Remove-Item $superseded -Force -ErrorAction SilentlyContinue
        Rename-Item -Path $target -NewName "$executableName.old" -Force
    }

    Move-Item -Path $temporary -Destination $target -Force
}
finally {
    Remove-Item $temporary -Force -ErrorAction SilentlyContinue
}

if (Add-ToUserPath $InstallDirectory) {
    Write-Host "Added $InstallDirectory to your PATH. Open a new terminal for that to apply."
}

Write-Host ''
Write-Host "Installed redux-launcher-cli $($selected.Version) to $target"
Write-Host 'Try: redux-launcher-cli --help'

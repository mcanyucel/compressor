#Requires -Version 7
<#
.SYNOPSIS
    Uploads the installer built by build.ps1 to the update server.

.DESCRIPTION
    Posts artifacts\Compressor-<version>-setup.exe (the version from Directory.Build.props) to the server's upload
    endpoint, then checks that the server recorded the same SHA-256. Installed copies offer the new version at their
    next start. The API key comes from the AUS_API_KEY environment variable; it never goes into the app.

    Same as uploading the file through the server's web interface with the app name "Compressor". The first upload
    creates the app on the server.

.EXAMPLE
    $env:AUS_API_KEY = '...'; .\installer\upload.ps1 -Notes "Shows check progress; optional locked-file check."
#>
param(
    # The release notes the update dialog shows: as text, or a file holding them.
    [string] $Notes = "",
    [string] $NotesFile = "",
    # Another installer than the one for the current version.
    [string] $File = "",
    [string] $Server = "https://software.mustafacanyucel.com"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$app = Join-Path $root "Ui\Ui.csproj"
$appName = "Compressor"       # the update server knows the app by this name (Ui\Updates\AppUpdates.cs)

$version = (dotnet msbuild $app -getProperty:Version).Trim()
if ($LASTEXITCODE -ne 0) { throw "Reading the version failed (exit code $LASTEXITCODE)." }
if (-not $File) { $File = Join-Path $root "artifacts\Compressor-$version-setup.exe" }
if (-not (Test-Path $File)) { throw "$File is missing. Build it first: .\installer\build.ps1" }

if ($NotesFile) { $Notes = (Get-Content $NotesFile -Raw).Trim() }
if (-not $Notes) { throw "Pass -Notes or -NotesFile with the release notes; the update dialog shows them." }
if (-not $env:AUS_API_KEY) { throw "Set AUS_API_KEY to the update server's API key." }

$hash = (Get-FileHash $File -Algorithm SHA256).Hash.ToLowerInvariant()
$size = [math]::Round((Get-Item $File).Length / 1MB, 1)
Write-Host "Uploading $appName $version ($size MB) to $Server"

# The server's POST /api/update/upload: a multipart form with appName, version, notes and updateFile.
# Invoke-RestMethod stops with the server's answer on any HTTP error (401 for a wrong key).
$form = @{ appName = $appName; version = $version; notes = $Notes; updateFile = Get-Item $File }
$uploaded = Invoke-RestMethod -Method Post -Uri "$($Server.TrimEnd('/'))/api/update/upload" `
    -Headers @{ "X-API-Key" = $env:AUS_API_KEY } -Form $form

if ($uploaded.sha256Hash -ne $hash) {
    throw "The server recorded SHA-256 $($uploaded.sha256Hash), not ${hash}; the upload was damaged. Upload again."
}
Write-Host "Uploaded $($uploaded.versionString). Installed copies will offer it at their next start."

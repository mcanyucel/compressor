#Requires -Version 7
<#
.SYNOPSIS
    Builds the Compressor installer: artifacts\Compressor-<version>-setup.exe.

.DESCRIPTION
    1. Checks that the working tree is committed and that the bundled 7-Zip has been fetched.
    2. Runs the tests.
    3. Publishes both exes (CompressorGui.exe and compressor.exe) self-contained for win-x64 into one folder,
       artifacts\publish, and checks that the published CLI reports the right version and that 7-Zip is there.
       Checks that THIRD-PARTY-NOTICES.txt matches what is shipped (make-notices.py) and adds it.
    4. Compiles installer\Compressor.iss into artifacts\Compressor-<version>-setup.exe.

    The version comes from Directory.Build.props; raise it there before a release. Upload the installer with
    upload.ps1 or through the update server's web interface; installed copies then offer it at their next start.

.EXAMPLE
    .\installer\build.ps1
.EXAMPLE
    .\installer\build.ps1 -AllowDirty -SkipTests     # a quick test build from uncommitted changes
#>
param(
    [switch] $SkipTests,
    [switch] $AllowDirty
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$gui = Join-Path $root "Ui\Ui.csproj"
$cli = Join-Path $root "Cli\Cli.csproj"
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish"

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Check-Exit($what) { if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." } }

$version = (dotnet msbuild $gui -getProperty:Version).Trim()
Check-Exit "Reading the version"
$publisher = (dotnet msbuild $gui -getProperty:Company).Trim()
Check-Exit "Reading the publisher"
Write-Host "Compressor $version ($publisher)"

if (-not $AllowDirty) {
    $dirty = git -C $root status --porcelain
    if ($dirty) { throw "The working tree has uncommitted changes. Commit them first, or pass -AllowDirty for a test build." }
}

if (-not (Test-Path (Join-Path $root "third-party\7zip\win-x64\7za.exe"))) {
    throw "The bundled 7-Zip is missing. Fetch it first: pwsh third-party\7zip\fetch.ps1"
}

$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 is not installed (ISCC.exe not found)." }

if (-not $SkipTests) {
    Step "Tests"
    dotnet test (Join-Path $root "Tests\Tests.csproj")
    Check-Exit "The tests"
}

Step "Publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
foreach ($project in $gui, $cli) {
    # Self-contained: the old PCs this is meant for may not have .NET. ReadyToRun: faster startup.
    dotnet publish $project -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o $publish
    Check-Exit "dotnet publish $(Split-Path $project -Leaf)"
}

$reported = (& (Join-Path $publish "compressor.exe") version | Out-String).Trim()
if ($reported -ne $version) { throw "The published CLI reports version '$reported', not $version." }
Write-Host "compressor version: $reported"

Step "Third-party notices"
# After publishing: the notices name the .NET runtime version the published app carries.
python -I (Join-Path $PSScriptRoot "make-notices.py") --check
Check-Exit "The notices check"
Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.txt") $publish
# The app's own license (MIT).
Copy-Item (Join-Path $root "LICENSE") (Join-Path $publish "LICENSE.txt")

foreach ($file in "7zip\win-x64\7za.exe", "7zip\License.txt", "7zip\SHA256SUMS", "CompressorGui.exe", "THIRD-PARTY-NOTICES.txt", "LICENSE.txt") {
    if (-not (Test-Path (Join-Path $publish $file))) { throw "$file is missing from the published app." }
}

Step "Installer"
& $iscc /Q "/DAppVersion=$version" "/DAppPublisher=$publisher" (Join-Path $PSScriptRoot "Compressor.iss")
Check-Exit "Inno Setup"
$setup = Join-Path $artifacts "Compressor-$version-setup.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "$setup ($size MB)"
Write-Host "SHA-256 $hash"

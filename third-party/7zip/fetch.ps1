#Requires -Version 7
<#
.SYNOPSIS
    Downloads the pinned 7-Zip release, verifies it, and unpacks the binaries the app bundles.

.DESCRIPTION
    Every downloaded file is checked against the SHA-256 pinned below (taken from the official release at
    https://github.com/ip7z/7zip/releases, which 7-zip.org links to). The unpacked binaries are then checked
    against SHA256SUMS, the same file the app checks at startup.

    Result, next to this script:
        win-x86/7za.exe  win-x64/7za.exe  win-arm64/7za.exe   (standalone console build from the "extra" package)
        linux-x64/7zz    linux-arm64/7zz  osx/7zz             (official console builds)
        License.txt      VERSION          SHA256SUMS

    The binaries are not committed to git; run this once after cloning. The build copies whatever is here into
    the app's output folder (see SevenZip.targets).

.PARAMETER UpdateChecksums
    Rewrite SHA256SUMS from the unpacked binaries instead of checking against it. Only needed after changing
    the pinned version below.
#>
param([switch]$UpdateChecksums)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ── Pinned release ───────────────────────────────────────────────────────────────────
# To upgrade: change $Version, replace the hashes with the ones shown on the GitHub release page,
# run with -UpdateChecksums, and commit VERSION, SHA256SUMS and License.txt.

$Version = '26.06'
$Compact = $Version -replace '\.', ''
$BaseUrl = "https://github.com/ip7z/7zip/releases/download/$Version"

$Downloads = [ordered]@{
    '7zr.exe'                       = 'e80db2dad715a6c5adabe292d31ed54e11f4e9b99ca349827b50573b175891bc'
    "7z$Compact-extra.7z"           = '396c180f12d2247c24b0b1fcd482c6fc2a2700f61211ffede1061dd971f5e104'
    "7z$Compact-linux-x64.tar.xz"   = '2b539cf067b2ad48e1baa5093dbaa8818b73542d1222a0ffde9dc5f2922ea62e'
    "7z$Compact-linux-arm64.tar.xz" = 'edcc408aacce77da5223728f2eceb5e9f6dc713b8a9df934ad21e2a7f7560627'
    "7z$Compact-mac.tar.xz"         = 'b6f17920b4240940461dac8ef4009891881ac44d2a5ead625ede2ec8472185dd'
}

# ── Download and verify ──────────────────────────────────────────────────────────────

$Here = $PSScriptRoot
$Work = Join-Path ([IO.Path]::GetTempPath()) "compressor-7zip-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Path $Work | Out-Null

function Get-Sha256([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }

function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$Exe $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

try {
    foreach ($name in $Downloads.Keys) {
        $target = Join-Path $Work $name
        Write-Host "Downloading $name"
        Invoke-WebRequest -Uri "$BaseUrl/$name" -OutFile $target -UseBasicParsing
        $actual = Get-Sha256 $target
        if ($actual -ne $Downloads[$name]) {
            throw "SHA-256 mismatch for $name`n  expected $($Downloads[$name])`n  got      $actual"
        }
    }

    # ── Unpack ───────────────────────────────────────────────────────────────────────
    $extra = Join-Path $Work 'extra'
    $tarballs = @{
        'linux-x64'   = "7z$Compact-linux-x64.tar.xz"
        'linux-arm64' = "7z$Compact-linux-arm64.tar.xz"
        'osx'         = "7z$Compact-mac.tar.xz"
    }

    if ($IsWindows) {
        # 7zr.exe only reads .7z: use it for the extra package, then that package's 7za.exe for the .tar.xz files.
        Invoke-Checked (Join-Path $Work '7zr.exe') @('x', (Join-Path $Work "7z$Compact-extra.7z"), "-o$extra", '-y')
        $hostArch = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
        $sevenZa = Join-Path $extra "$hostArch/7za.exe"

        foreach ($platform in $tarballs.Keys) {
            $out = Join-Path $Work $platform
            Invoke-Checked $sevenZa @('x', (Join-Path $Work $tarballs[$platform]), "-o$out", '-y')
            $tar = Get-ChildItem -LiteralPath $out -Filter '*.tar' | Select-Object -First 1
            Invoke-Checked $sevenZa @('x', $tar.FullName, "-o$out", '-y')
        }
    }
    else {
        foreach ($platform in $tarballs.Keys) {
            $out = Join-Path $Work $platform
            New-Item -ItemType Directory -Path $out | Out-Null
            Invoke-Checked 'tar' @('-xJf', (Join-Path $Work $tarballs[$platform]), '-C', $out)
        }
        # The host's own 7zz unpacks the Windows "extra" package.
        $hostPlatform = if ($IsMacOS) { 'osx' }
                        elseif ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'linux-arm64' }
                        else { 'linux-x64' }
        Invoke-Checked (Join-Path $Work "$hostPlatform/7zz") @('x', (Join-Path $Work "7z$Compact-extra.7z"), "-o$extra", '-y')
    }

    # ── Copy into place ──────────────────────────────────────────────────────────────
    $layout = [ordered]@{
        'win-x86/7za.exe'   = Join-Path $extra '7za.exe'
        'win-x64/7za.exe'   = Join-Path $extra 'x64/7za.exe'
        'win-arm64/7za.exe' = Join-Path $extra 'arm64/7za.exe'
        'linux-x64/7zz'     = Join-Path $Work 'linux-x64/7zz'
        'linux-arm64/7zz'   = Join-Path $Work 'linux-arm64/7zz'
        'osx/7zz'           = Join-Path $Work 'osx/7zz'
    }

    foreach ($relative in $layout.Keys) {
        $destination = Join-Path $Here $relative
        New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
        Copy-Item -LiteralPath $layout[$relative] -Destination $destination -Force
    }
    Copy-Item -LiteralPath (Join-Path $extra 'License.txt') -Destination (Join-Path $Here 'License.txt') -Force
    Set-Content -LiteralPath (Join-Path $Here 'VERSION') -Value $Version -NoNewline

    # ── Checksums the app verifies at startup ────────────────────────────────────────
    $sumsPath = Join-Path $Here 'SHA256SUMS'
    $lines = foreach ($relative in $layout.Keys) { "$(Get-Sha256 (Join-Path $Here $relative))  $relative" }

    if ($UpdateChecksums -or -not (Test-Path -LiteralPath $sumsPath)) {
        Set-Content -LiteralPath $sumsPath -Value (($lines -join "`n") + "`n") -NoNewline
        Write-Host "Wrote SHA256SUMS. Commit it."
    }
    else {
        $expected = (Get-Content -LiteralPath $sumsPath) | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() }
        $diff = Compare-Object -ReferenceObject $expected -DifferenceObject $lines
        if ($diff) { throw "Unpacked binaries don't match SHA256SUMS:`n$($diff | Out-String)" }
        Write-Host "Binaries match SHA256SUMS."
    }

    Write-Host "7-Zip $Version ready in $Here"
}
finally {
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
}

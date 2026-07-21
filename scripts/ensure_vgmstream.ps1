param(
    [string]$RuntimeDirectory = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($RuntimeDirectory)) {
    $RuntimeDirectory = Join-Path $repositoryRoot ".tools\vgmstream"
}
$resolvedRuntimeDirectory = [IO.Path]::GetFullPath($RuntimeDirectory)
$repositoryPrefix = $repositoryRoot.TrimEnd('\') + '\'
if (-not $resolvedRuntimeDirectory.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The vgmstream runtime must stay inside the standalone repository: $resolvedRuntimeDirectory"
}

$version = "r1980"
$buildCommit = "21bfb6f0a513271f2e18a51322128756bb59f365"
$archiveSha256 = "110f9087e60057c4af6cff84e26c214159c224792421affdddd3aaa2091f2641"
$downloadUrl = "https://github.com/bnnm/vgmstream-builds/raw/$buildCommit/bin/vgmstream-$version-test-u.zip"
$manifestPath = Join-Path $resolvedRuntimeDirectory ".cdmw-dependency.json"

function Test-PinnedRuntime {
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return $false
    }
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ([string]$manifest.version -ne $version -or
            [string]$manifest.build_commit -ne $buildCommit -or
            [string]$manifest.archive_sha256 -ne $archiveSha256) {
            return $false
        }
        $files = @($manifest.files.PSObject.Properties)
        if (-not $files) {
            return $false
        }
        foreach ($file in $files) {
            $path = Join-Path $resolvedRuntimeDirectory $file.Name
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                return $false
            }
            $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actualHash -ne ([string]$file.Value).ToLowerInvariant()) {
                return $false
            }
        }
        return $true
    }
    catch {
        return $false
    }
}

if (Test-PinnedRuntime) {
    Write-Host "Pinned vgmstream runtime is ready ($version)."
    exit 0
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("cdmw-lite-vgmstream-" + [Guid]::NewGuid().ToString("N"))
$archivePath = Join-Path $tempRoot "vgmstream.zip"
$extractRoot = Join-Path $tempRoot "extract"
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
try {
    Write-Host "Downloading pinned vgmstream runtime $version..."
    Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath -UseBasicParsing
    $actualArchiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualArchiveHash -ne $archiveSha256) {
        throw "vgmstream archive SHA-256 mismatch. Expected $archiveSha256, got $actualArchiveHash."
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot
    $cli = Get-ChildItem -LiteralPath $extractRoot -Recurse -Filter "vgmstream-cli.exe" -File | Select-Object -First 1
    if ($null -eq $cli) {
        throw "Downloaded vgmstream archive did not contain vgmstream-cli.exe."
    }
    $runtimeFiles = @(Get-ChildItem -LiteralPath $cli.DirectoryName -File | Where-Object {
        $_.Name -eq "vgmstream-cli.exe" -or $_.Extension -ieq ".dll" -or $_.Name -eq "COPYING"
    })
    if ($runtimeFiles.Count -lt 2) {
        throw "Downloaded vgmstream archive did not contain the expected runtime files."
    }

    $versionJsonOutput = (& $cli.FullName -V 2>$null | Out-String).Trim()
    $probeExitCode = $LASTEXITCODE
    if ($probeExitCode -ne 1) {
        throw "Downloaded vgmstream runtime probe returned unexpected exit code $probeExitCode."
    }
    $versionJson = $versionJsonOutput | ConvertFrom-Json
    if ([string]$versionJson.version -ne $version) {
        throw "Downloaded vgmstream runtime did not report version $version."
    }

    New-Item -ItemType Directory -Path $resolvedRuntimeDirectory -Force | Out-Null
    $fileHashes = [ordered]@{}
    foreach ($file in $runtimeFiles | Sort-Object Name) {
        $destination = Join-Path $resolvedRuntimeDirectory $file.Name
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        $fileHashes[$file.Name] = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    [ordered]@{
        schema = 1
        version = $version
        build_commit = $buildCommit
        archive_sha256 = $archiveSha256
        files = $fileHashes
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
}
finally {
    $resolvedTempRoot = [IO.Path]::GetFullPath($tempRoot)
    $systemTempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedTempRoot.StartsWith($systemTempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTempRoot)) {
        [IO.Directory]::Delete($resolvedTempRoot, $true)
    }
}

if (-not (Test-PinnedRuntime)) {
    throw "The downloaded vgmstream runtime failed its pinned-file verification."
}
Write-Host "Pinned vgmstream runtime installed ($version)."
exit 0

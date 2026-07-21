Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$repositoryPrefix = $repositoryRoot.TrimEnd('\') + '\'

$gitRoot = (& git -C $repositoryRoot rev-parse --show-toplevel 2>$null | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitRoot)) {
    throw "CDMW Lite must be built from its own Git repository."
}
$resolvedGitRoot = [IO.Path]::GetFullPath($gitRoot)
if (-not $resolvedGitRoot.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The Git root '$resolvedGitRoot' does not match the CDMW Lite root '$repositoryRoot'."
}

$requiredPaths = @(
    "Cdmw.ArchiveLite.slnx",
    "src\Cdmw.Archive.Content\Cdmw.Archive.Content.csproj",
    "schemas\archive_content_capabilities.v1.json",
    "native\common\native_diagnostics.h",
    "native\cdmw_archive_core\CMakeLists.txt",
    "native\cdmw_preview_core\CMakeLists.txt",
    "native\cdmw_archive_accelerator\CMakeLists.txt",
    "native\cdmw_mesh_core\CMakeLists.txt",
    "native\cd_texture_dx\CMakeLists.txt",
    "tools\dotnet_mesh_editor_experiment\Cdmw.MeshEditorExperiment.csproj"
)
foreach ($relativePath in $requiredPaths) {
    $requiredPath = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "The standalone repository is missing required source: $relativePath"
    }
}

function Assert-ContainedReference([string]$OwnerPath, [string]$ReferencePath) {
    if ([string]::IsNullOrWhiteSpace($ReferencePath) -or $ReferencePath.Contains('$(')) {
        return
    }
    $resolved = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $OwnerPath) $ReferencePath))
    if (-not $resolved.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Reference '$ReferencePath' in '$OwnerPath' escapes the standalone repository."
    }
    if (-not (Test-Path -LiteralPath $resolved)) {
        throw "Reference '$ReferencePath' in '$OwnerPath' does not exist."
    }
}

$solutionPath = Join-Path $repositoryRoot "Cdmw.ArchiveLite.slnx"
[xml]$solution = Get-Content -LiteralPath $solutionPath -Raw
foreach ($project in @($solution.SelectNodes("//*[local-name()='Project']"))) {
    Assert-ContainedReference -OwnerPath $solutionPath -ReferencePath ([string]$project.GetAttribute("Path"))
}

foreach ($projectPath in Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "src") -Recurse -Filter "*.csproj" -File) {
    [xml]$project = Get-Content -LiteralPath $projectPath.FullName -Raw
    foreach ($reference in @($project.SelectNodes("//*[local-name()='ProjectReference']"))) {
        Assert-ContainedReference -OwnerPath $projectPath.FullName -ReferencePath ([string]$reference.GetAttribute("Include"))
    }
    foreach ($resource in @($project.SelectNodes("//*[local-name()='EmbeddedResource']"))) {
        Assert-ContainedReference -OwnerPath $projectPath.FullName -ReferencePath ([string]$resource.GetAttribute("Include"))
    }
}

$legacyRepositoryName = "app_" + "restructuring"
$legacyAppPath = "apps" + [IO.Path]::DirectorySeparatorChar + "Cdmw.ArchiveLite"
$verifierPath = [IO.Path]::GetFullPath($MyInvocation.MyCommand.Path)
$textExtensions = @(".bat", ".cs", ".csproj", ".json", ".md", ".props", ".ps1", ".slnx", ".xaml", ".yaml", ".yml")
$excludedSegments = @("\.git\", "\.tools\", "\artifacts\", "\bin\", "\build\", "\obj\")
$legacyReferences = foreach ($file in Get-ChildItem -LiteralPath $repositoryRoot -Recurse -File) {
    $isExcluded = $false
    foreach ($segment in $excludedSegments) {
        if ($file.FullName.IndexOf($segment, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $isExcluded = $true
            break
        }
    }
    if ($file.FullName.Equals($verifierPath, [StringComparison]::OrdinalIgnoreCase) -or
        $textExtensions -notcontains $file.Extension.ToLowerInvariant() -or
        $isExcluded) {
        continue
    }
    $text = [IO.File]::ReadAllText($file.FullName)
    if ($text.IndexOf($legacyRepositoryName, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $text.IndexOf($legacyAppPath, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $text.IndexOf($legacyAppPath.Replace('\', '/'), [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $file.FullName.Substring($repositoryPrefix.Length)
    }
}
if ($legacyReferences) {
    throw "Standalone source still refers to the former monorepo layout: $($legacyReferences -join ', ')"
}

Write-Host "Repository independence guard passed: all required sources and references are contained in $repositoryRoot."

<#
.SYNOPSIS
    Checks, verifies and packs an Instella release from a tag.

.DESCRIPTION
    The same script runs locally (a dry run of a release) and in .github/workflows/release.yml,
    which publishes. Publishing itself (nuget.org through trusted publishing, the server image to
    ghcr.io, the GitHub release) happens only in the workflow, so no long-lived key is needed.

      1. The working tree is clean and HEAD is the tagged commit.
      2. The tag is v<prefix> or v<prefix>-(rc|beta|preview).<n>, and <prefix> is VersionPrefix
         in Directory.Build.props.
      3. CHANGELOG.md has a "## [<prefix>]" section; a final release's heading carries a date.
      4. ./scripts/verify.ps1 -NoSkip -VersionSuffix <suffix> -Stage <GateStages> passes.
      5. dotnet pack into artifacts/<tag>; every package has the expected version, and Core, Sdk,
         Runtime and Testing have symbol packages.
      6. scripts/build.ps1 builds the server image (skipped with -SkipImage).

    -CheckOnly stops after step 3. It writes the CHANGELOG section to -NotesFile when given, and
    when GITHUB_OUTPUT is set (in a workflow) it writes version, suffix and prerelease there.
    -RequireBranch master also refuses a tag whose commit is not on that branch of origin, so
    only reviewed, merged code is released.

.PARAMETER Tag
    v0.1.0 for a release, v0.2.0-rc.1 for a release candidate.

.PARAMETER GateStages
    The verify.ps1 stages to run in step 4. The release workflow runs the Windows stages and the
    Docker stage in separate jobs, since a Windows runner cannot run Linux containers.

.EXAMPLE
    ./scripts/release.ps1 -Tag v0.2.0-rc.1
    ./scripts/release.ps1 -Tag v0.1.0 -CheckOnly -NotesFile notes.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [switch]$CheckOnly,
    [string]$NotesFile,
    [string]$RequireBranch,
    [string[]]$GateStages = @('All'),
    [switch]$SkipImage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$imageName = 'instella-server'
# The packages that ship symbols (Build and the CLI tool turn them off).
$symbolPackages = @('Instella.Core', 'Instella.Sdk', 'Instella.Installer.Runtime', 'Instella.Installer.Testing')

function Step([string]$Text) { Write-Host "`n=== $Text ===" -ForegroundColor Cyan }
function Fail([string]$Text) { Write-Host "release: $Text" -ForegroundColor Red; exit 1 }

function Invoke-Native {
    param([Parameter(Mandatory)][string]$File, [string[]]$Arguments = @())
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { Fail "$File $($Arguments -join ' ') exited $LASTEXITCODE" }
}

# The id and version recorded in a .nupkg's .nuspec.
function Get-NupkgIdentity([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -notmatch '/' -and $_.Name -like '*.nuspec' } | Select-Object -First 1
        if (-not $entry) { return $null }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { $xml = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
        $ns = [System.Xml.XmlNamespaceManager]::new($xml.NameTable)
        $ns.AddNamespace('n', $xml.DocumentElement.NamespaceURI)
        [pscustomobject]@{
            Id      = $xml.SelectSingleNode('/n:package/n:metadata/n:id', $ns).InnerText
            Version = $xml.SelectSingleNode('/n:package/n:metadata/n:version', $ns).InnerText
        }
    }
    finally { $zip.Dispose() }
}

Push-Location $repoRoot
try {
    Step "Tree and tag"
    $dirty = git status --porcelain
    if ($LASTEXITCODE -ne 0) { Fail 'git status failed' }
    if ($dirty) { Fail "the working tree is not clean:`n$($dirty -join "`n")" }
    $head = git rev-parse HEAD
    $tagged = git rev-parse --verify --quiet "$Tag^{commit}"
    if ($LASTEXITCODE -ne 0 -or -not $tagged) { Fail "tag $Tag does not exist (create it first: git tag -a $Tag)" }
    if ($head -ne $tagged) { Fail "HEAD ($head) is not the commit tagged $Tag ($tagged); check out the tag" }
    if ($RequireBranch) {
        git fetch --quiet --no-tags origin $RequireBranch
        if ($LASTEXITCODE -ne 0) { Fail "could not fetch $RequireBranch from origin" }
        git merge-base --is-ancestor $tagged "origin/$RequireBranch"
        if ($LASTEXITCODE -ne 0) { Fail "$Tag ($tagged) is not on $RequireBranch; tag a commit that has been merged" }
    }

    Step "Version"
    if ($Tag -notmatch '^v(\d+\.\d+\.\d+)(?:-((?:rc|beta|preview)\.\d+))?$') {
        Fail "tag $Tag must look like v0.1.0 or v0.2.0-rc.1 (rc, beta or preview)"
    }
    $prefix = $Matches[1]
    $suffix = if ($Matches.Count -gt 2 -and $Matches[2]) { $Matches[2] } else { '' }
    $version = if ($suffix) { "$prefix-$suffix" } else { $prefix }
    $isFinal = -not $suffix
    $props = [xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props'))
    $propsPrefix = $props.SelectSingleNode('/Project/PropertyGroup/VersionPrefix')
    if (-not $propsPrefix) { Fail 'VersionPrefix not found in Directory.Build.props' }
    if ($propsPrefix.InnerText.Trim() -ne $prefix) {
        Fail "the tag's version $prefix differs from VersionPrefix $($propsPrefix.InnerText.Trim()) in Directory.Build.props"
    }
    Write-Host "Version $version ($(if ($isFinal) { 'release' } else { 'pre-release' }))"

    Step "CHANGELOG"
    $changelog = Get-Content (Join-Path $repoRoot 'CHANGELOG.md')
    $escaped = [regex]::Escape($prefix)
    $start = -1
    for ($i = 0; $i -lt $changelog.Count; $i++) {
        if ($changelog[$i] -match "^## \[$escaped\]") { $start = $i; break }
    }
    if ($start -lt 0) { Fail "CHANGELOG.md has no '## [$prefix]' section" }
    if ($isFinal -and $changelog[$start] -notmatch "^## \[$escaped\] - \d{4}-\d{2}-\d{2}\s*$") {
        Fail "the CHANGELOG heading for a release needs its date ('## [$prefix] - YYYY-MM-DD'), found '$($changelog[$start])'"
    }
    $end = $changelog.Count
    for ($i = $start + 1; $i -lt $changelog.Count; $i++) {
        # The next version's section, or the link definitions at the end of the file.
        if ($changelog[$i] -match '^## \[' -or $changelog[$i] -match '^\[[^\]]+\]: ') { $end = $i; break }
    }
    $notes = ($changelog[($start + 1)..($end - 1)] -join "`n").Trim()
    if ($NotesFile) { Set-Content -Path $NotesFile -Value $notes -Encoding utf8 }
    if ($env:GITHUB_OUTPUT) {
        Add-Content -Path $env:GITHUB_OUTPUT -Value "version=$version", "suffix=$suffix", "prerelease=$((-not $isFinal).ToString().ToLowerInvariant())"
    }
    if ($CheckOnly) { return }

    Step "Verification gate ($($GateStages -join ','), -NoSkip)"
    # verify.ps1 packs into its own scratch feed and discards it; the next step packs again with the same settings.
    & (Join-Path $PSScriptRoot 'verify.ps1') -Stage $GateStages -NoSkip -VersionSuffix $suffix
    if ($LASTEXITCODE -ne 0) { Fail 'verify.ps1 failed' }

    Step "Pack"
    $outDir = Join-Path $repoRoot "artifacts/$Tag"
    if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
    # ContinuousIntegrationBuild: deterministic PDB paths in the published packages.
    $env:CI = 'true'
    Invoke-Native 'dotnet' @('pack', (Join-Path $repoRoot 'Instella.sln'), '-c', 'Release', '-o', $outDir,
        '-nodeReuse:false', "-p:VersionSuffix=$suffix")
    $nupkgs = @(Get-ChildItem $outDir -Filter '*.nupkg' -File)
    if ($nupkgs.Count -eq 0) { Fail 'dotnet pack produced no packages' }
    foreach ($nupkg in $nupkgs) {
        $identity = Get-NupkgIdentity $nupkg.FullName
        if (-not $identity) { Fail "$($nupkg.Name) has no .nuspec" }
        if ($identity.Version -ne $version) { Fail "$($identity.Id) is version $($identity.Version), expected $version" }
    }
    foreach ($id in $symbolPackages) {
        if (-not (Test-Path (Join-Path $outDir "$id.$version.snupkg"))) { Fail "no symbol package for $id ($id.$version.snupkg)" }
    }
    Write-Host "$($nupkgs.Count) packages at $version in $outDir"

    if (-not $SkipImage) {
        Step "Server image"
        & (Join-Path $PSScriptRoot 'build.ps1') -Version $version -ImageName $imageName -OutputDir $outDir
        if (-not $?) { Fail 'build.ps1 failed' }
    }

    Step "Release notes"
    Write-Host "## [$prefix]$(if ($suffix) { " ($version)" })`n"
    Write-Host $notes
    Write-Host "`nNothing was published. Push the tag to run .github/workflows/release.yml."
}
finally {
    Pop-Location
}

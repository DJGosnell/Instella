# Instella Server Build Script (Windows)
# Builds and exports a versioned Docker image for deployment

param(
    [string]$Version,

    # The local image name; scripts/release.ps1 re-tags it for a registry.
    [string]$ImageName = "instella-server",

    [string]$OutputDir = ".\dist"
)

$ErrorActionPreference = "Stop"

# Ensure we're in the repo root
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

# Detect version from Directory.Build.props if not provided.
# VersionPrefix in Directory.Build.props is the single source of version truth for the
# whole repository, packages and container image alike (no project sets its own <Version>).
if (-not $Version) {
    $propsPath = "Directory.Build.props"
    if (Test-Path $propsPath) {
        [xml]$props = Get-Content $propsPath
        # PropertyGroup is an array whenever the file declares more than one, so
        # take the first non-empty VersionPrefix instead of indexing a fixed slot.
        $Version = $props.Project.PropertyGroup.VersionPrefix |
            Where-Object { $_ } |
            Select-Object -First 1
        if ($Version) {
            Write-Host "Detected version from Directory.Build.props: $Version" -ForegroundColor Gray
        }
    }
    if (-not $Version) {
        throw "Version not specified and could not be detected from $propsPath (VersionPrefix). Use -Version parameter."
    }
}

$imageName = $ImageName
$imageTag = "${imageName}:${Version}"
$tarFile = "$(($imageName -split '/')[-1])-${Version}.tar"
# A pre-release (0.2.0-rc.1) never becomes :latest.
$isFinal = -not $Version.Contains('-')
$publishDir = "src\Instella.Server\publish"

Write-Host "Building Instella Server v${Version}..." -ForegroundColor Cyan

try {
    # Create output directory if it doesn't exist
    if (-not (Test-Path $OutputDir)) {
        New-Item -ItemType Directory -Path $OutputDir | Out-Null
        Write-Host "Created output directory: $OutputDir" -ForegroundColor Gray
    }

    # Step 1: Build and publish with dotnet
    Write-Host "`nStep 1/4: Building .NET application..." -ForegroundColor Yellow

    # Clean previous publish output
    if (Test-Path $publishDir) {
        Remove-Item -Recurse -Force $publishDir
    }

    dotnet publish src/Instella.Server/Instella.Server.csproj `
        -c Release `
        -o $publishDir `
        /p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    # Step 2: Build the Docker image
    Write-Host "`nStep 2/4: Building Docker image..." -ForegroundColor Yellow
    docker build -t $imageTag -f src/Instella.Server/Dockerfile src/Instella.Server
    if ($LASTEXITCODE -ne 0) { throw "Docker build failed" }

    # Step 3: Tag as latest (final releases only)
    if ($isFinal) {
        Write-Host "`nStep 3/4: Tagging as latest..." -ForegroundColor Yellow
        docker tag $imageTag "${imageName}:latest"
        if ($LASTEXITCODE -ne 0) { throw "Docker tag failed" }
    }
    else {
        Write-Host "`nStep 3/4: Pre-release ${Version}: not tagging as latest" -ForegroundColor Yellow
    }

    # Step 4: Export to tar file
    Write-Host "`nStep 4/4: Exporting image to ${tarFile}..." -ForegroundColor Yellow
    $outputPath = Join-Path $OutputDir $tarFile
    docker save $imageTag -o $outputPath
    if ($LASTEXITCODE -ne 0) { throw "Docker save failed" }

    # Clean up publish directory
    Remove-Item -Recurse -Force $publishDir

    $fileSize = [math]::Round((Get-Item $outputPath).Length / 1MB, 2)

    Write-Host "`nBuild complete!" -ForegroundColor Green
    Write-Host "  Image: $imageTag" -ForegroundColor Gray
    Write-Host "  Output: $outputPath ($fileSize MB)" -ForegroundColor Gray
    Write-Host "`nTo deploy, copy these files to your server:" -ForegroundColor Cyan
    Write-Host "  - $outputPath"
    Write-Host "  - deploy/docker-compose.yml"
    Write-Host "  - deploy/config/appsettings.json (as config/appsettings.json, next to the compose file)"
    Write-Host "  - .env (create from template below)"
    Write-Host "`n.env template:" -ForegroundColor Cyan
    Write-Host "  VERSION=$Version"
    Write-Host "  INSTELLA_PORT=8580"
}
finally {
    Pop-Location
}

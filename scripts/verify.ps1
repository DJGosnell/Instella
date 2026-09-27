<#
.SYNOPSIS
    Local verification gate for the Instella repository.

.DESCRIPTION
    There is no CI yet, so run the stages a change touches and paste the
    summary into the pull request (docs/verification.md).

    Stages (run in this order by -Stage All; the run stops at the first FAIL):

      Build       dotnet build of the solution; warnings are errors
      Test        dotnet test of the solution, excluding Category=E2E
      Migrations  the server's EF model has no changes missing a migration
      Audit       dotnet list package --vulnerable --include-transitive
      Pack        pack to a local feed, assert version + licence, then publish
                  a generated consumer from that feed: footer and stub present,
                  a republish is byte-identical and drops stale files, and
                  win-arm64, linux-x64 (INSTELLA0001) and UseArtifactsOutput work
      Signing     publish the Pack stage's consumer with InstellaSignCommand and a
                  throwaway certificate; it must install --silent and stage a
                  signed instella.exe
      Aot         PublishAot of the sample installer; the exe must run alone
      E2E         tests/Instella.E2E.Tests: server on Kestrel, CLI, a real installer
                  and updater - install, update, crash recovery, tamper, uninstall,
                  leak check (Windows; installs into the user profile)
      Docker      build the server image, run it with empty volumes, /healthz,
                  upload, restart, verify persistence (needs Docker)

    A stage whose tooling is missing (MSVC linker, Docker, signtool) reports
    SKIP and names the missing prerequisite. SKIP is never PASS: a release
    candidate needs every stage to PASS.

.PARAMETER Stage
    One or more stage names, or All (the default).

.PARAMETER KeepTemp
    Leave the scratch directory in place for inspection.

.PARAMETER VersionSuffix
    Pack with -p:VersionSuffix=<suffix> (for example rc.1) and expect packages at
    <VersionPrefix>-<suffix>. scripts/release.ps1 passes it for a release candidate.

.PARAMETER NoSkip
    Treat a skipped stage as a failure (exit 1). CI passes this, so a runner that
    lost a prerequisite goes red instead of silently green.

.EXAMPLE
    ./scripts/verify.ps1
    ./scripts/verify.ps1 -Stage Build,Test
    ./scripts/verify.ps1 -NoSkip -VersionSuffix rc.1
#>
[CmdletBinding()]
param(
    [ValidateSet('All', 'Build', 'Test', 'Migrations', 'Audit', 'Pack', 'Signing', 'Aot', 'E2E', 'Docker')]
    [string[]]$Stage = @('All'),
    [string]$Configuration = 'Release',
    [switch]$KeepTemp,
    [switch]$NoSkip,
    [ValidatePattern('^([0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$')]
    [string]$VersionSuffix = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$allStages = @('Build', 'Test', 'Migrations', 'Audit', 'Pack', 'Signing', 'Aot', 'E2E', 'Docker')
$selected = if ($Stage -contains 'All') { $allStages } else { $allStages | Where-Object { $Stage -contains $_ } }

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'Instella.sln'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "instella-verify-$([System.Guid]::NewGuid().ToString('N'))"
$feedDir = Join-Path $tempRoot 'feed'

$results = [System.Collections.Generic.List[object]]::new()

function Add-Result {
    param(
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][ValidateSet('PASS', 'FAIL', 'SKIP')][string]$Status,
        [string]$Detail = ''
    )
    $results.Add([pscustomobject]@{ Stage = $Stage; Status = $Status; Detail = $Detail })
    $colour = switch ($Status) { 'PASS' { 'Green' } 'FAIL' { 'Red' } 'SKIP' { 'Yellow' } }
    Write-Host ("  [{0}] {1} {2}" -f $Status, $Stage, $Detail) -ForegroundColor $colour
}

function Write-Stage {
    param([Parameter(Mandatory)][string]$Title)
    Write-Host ''
    Write-Host "=== $Title ===" -ForegroundColor Cyan
}

# Runs a command, streaming output to the host while capturing it for parsing.
function Invoke-Capture {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [string]$WorkingDirectory = $repoRoot
    )
    Push-Location $WorkingDirectory
    try {
        $output = & $FilePath @Arguments 2>&1 | ForEach-Object {
            Write-Host "    $_"
            $_
        }
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output | Out-String) }
    }
    finally {
        Pop-Location
    }
}

# True when the file ends with an Instella footer (last 8 bytes are "INSTELLA").
function Test-FooterMagic {
    param([Parameter(Mandatory)][string]$Path)
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 16) { return $false }
        $stream.Seek(-8, [System.IO.SeekOrigin]::End) | Out-Null
        $buffer = New-Object byte[] 8
        if ($stream.Read($buffer, 0, 8) -ne 8) { return $false }
        return [System.Text.Encoding]::ASCII.GetString($buffer) -eq 'INSTELLA'
    }
    finally {
        $stream.Dispose()
    }
}

# Zip entry names of an installer's payload (footer v3: archive offset/length at 16/24 of
# the 80-byte footer that ends the file).
function Get-PayloadEntries {
    param([Parameter(Mandatory)][string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $footer = $bytes.Length - 80
    $offset = [System.BitConverter]::ToInt64($bytes, $footer + 16)
    $length = [System.BitConverter]::ToInt64($bytes, $footer + 24)
    $zipStream = [System.IO.MemoryStream]::new($bytes, [int]$offset, [int]$length)
    $zip = [System.IO.Compression.ZipArchive]::new($zipStream, [System.IO.Compression.ZipArchiveMode]::Read)
    try { return @($zip.Entries | ForEach-Object { $_.FullName }) }
    finally { $zip.Dispose() }
}

# Id, version and licence recorded in a .nupkg's .nuspec, plus the raw .nuspec and README text.
function Get-PackageMetadata {
    param([Parameter(Mandatory)][string]$Path)
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -like '*.nuspec' -and $_.FullName -notlike '*/*' } | Select-Object -First 1
        if (-not $entry) { return $null }
        $reader = New-Object System.IO.StreamReader($entry.Open())
        try { $nuspecXml = [xml]$reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $readmeEntry = $archive.Entries | Where-Object { $_.FullName -eq 'README.md' } | Select-Object -First 1
        $readme = $null
        if ($readmeEntry) {
            $readmeReader = New-Object System.IO.StreamReader($readmeEntry.Open())
            try { $readme = $readmeReader.ReadToEnd() } finally { $readmeReader.Dispose() }
        }
        $ns = New-Object System.Xml.XmlNamespaceManager($nuspecXml.NameTable)
        $ns.AddNamespace('n', $nuspecXml.DocumentElement.NamespaceURI)
        $select = {
            param($xpath)
            $node = $nuspecXml.SelectSingleNode($xpath, $ns)
            if ($node) { $node.InnerText.Trim() } else { $null }
        }
        return [pscustomobject]@{
            Id      = & $select '/n:package/n:metadata/n:id'
            Version = & $select '/n:package/n:metadata/n:version'
            License = & $select '/n:package/n:metadata/n:license'
            Nuspec  = $nuspecXml.OuterXml
            Readme  = $readme
        }
    }
    finally {
        $archive.Dispose()
    }
}

# AOT publishing needs the MSVC linker; vswhere.exe is the marker for it.
function Find-VsWhere {
    $onPath = Get-Command 'vswhere.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    if (${env:ProgramFiles(x86)}) {
        $standard = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path $standard) { return $standard }
    }
    return $null
}

$isWindowsHost = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows)

$propsXml = [xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props'))
$versionNode = $propsXml.SelectSingleNode('/Project/PropertyGroup/VersionPrefix')
if (-not $versionNode) { throw 'VersionPrefix not found in Directory.Build.props' }
$expectedVersion = $versionNode.InnerText.Trim()
# The repository location: package links derive from these, nothing else may name the host.
$repositoryUrl = $propsXml.SelectSingleNode('/Project/PropertyGroup/InstellaRepositoryUrl').InnerText.Trim()
$repositoryHost = $propsXml.SelectSingleNode('/Project/PropertyGroup/InstellaRepositoryHost').InnerText.Trim()
if ($VersionSuffix) { $expectedVersion = "$expectedVersion-$VersionSuffix" }

# Build the way CI would: ContinuousIntegrationBuild normalises PDB paths.
$env:CI = 'true'

# ----------------------------------------------------------------------
# Stages. Each returns nothing and records exactly one result.
#
# -nodeReuse:false is REQUIRED on builds: persistent MSBuild nodes keep a lock
# on Instella.Installer.Build.dll and the next build fails MSB3027/MSB3021.
# ----------------------------------------------------------------------

function Invoke-BuildStage {
    $build = Invoke-Capture 'dotnet' @('build', $solution, '-c', $Configuration, '-nodeReuse:false')
    if ($build.ExitCode -ne 0) {
        Add-Result 'Build' 'FAIL' "dotnet build exited $($build.ExitCode)"
        return
    }
    # Anchor on a line start: a bare '0 Warning(s)' also matches '10 Warning(s)'.
    $warningCount = if ($build.Output -match '(?m)^\s*(\d+) Warning\(s\)') { [int]$Matches[1] } else { -1 }
    if ($warningCount -lt 0) { Add-Result 'Build' 'FAIL' 'could not find a warning count in the build output' }
    elseif ($warningCount -ne 0) { Add-Result 'Build' 'FAIL' "build succeeded but emitted $warningCount warning(s)" }
    else { Add-Result 'Build' 'PASS' '0 warnings, 0 errors' }
}

function Invoke-TestStage {
    # No fixed expected counts: they broke on every added test and added no
    # safety. The gate is "tests ran and none failed".
    $test = Invoke-Capture 'dotnet' @('test', $solution, '-c', $Configuration, '-nodeReuse:false',
        '--filter', 'TestCategory!=E2E&TestCategory!=Docker&TestCategory!=Signing')
    $passed = 0; $failed = 0; $skipped = 0
    $summaries = [regex]::Matches($test.Output, 'Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+),\s+Total:\s+(\d+)')
    foreach ($m in $summaries) {
        $failed += [int]$m.Groups[1].Value
        $passed += [int]$m.Groups[2].Value
        $skipped += [int]$m.Groups[3].Value
    }
    $counts = "$passed passed, $failed failed, $skipped skipped across $($summaries.Count) assemblies"
    if ($summaries.Count -eq 0) { Add-Result 'Test' 'FAIL' 'could not parse any test summary - did any tests run?' }
    elseif ($test.ExitCode -ne 0 -or $failed -gt 0) { Add-Result 'Test' 'FAIL' $counts }
    elseif ($passed -eq 0) { Add-Result 'Test' 'FAIL' 'no tests passed' }
    else { Add-Result 'Test' 'PASS' $counts }
}

# Every schema change ships as an EF migration, so an existing server database upgrades
# in place at startup. dotnet-ef is pinned in
# the repo's tool manifest (dotnet-tools.json).
function Invoke-MigrationsStage {
    $restore = Invoke-Capture 'dotnet' @('tool', 'restore')
    if ($restore.ExitCode -ne 0) {
        Add-Result 'Migrations' 'FAIL' "dotnet tool restore exited $($restore.ExitCode)"
        return
    }
    $check = Invoke-Capture 'dotnet' @('tool', 'run', 'dotnet-ef', 'migrations', 'has-pending-model-changes',
        '--project', (Join-Path $repoRoot 'src/Instella.Server'))
    if ($check.ExitCode -ne 0) {
        Add-Result 'Migrations' 'FAIL' 'the model has changes without a migration (dotnet ef migrations add <Name>)'
    }
    else {
        Add-Result 'Migrations' 'PASS' 'no pending model changes'
    }
}

function Invoke-AuditStage {
    $audit = Invoke-Capture 'dotnet' @('list', $solution, 'package', '--vulnerable', '--include-transitive')
    if ($audit.ExitCode -ne 0) {
        Add-Result 'Audit' 'FAIL' "dotnet list package exited $($audit.ExitCode)"
    }
    elseif ($audit.Output -match 'has the following vulnerable packages') {
        Add-Result 'Audit' 'FAIL' 'vulnerable packages found (see output above)'
    }
    else {
        Add-Result 'Audit' 'PASS' 'no known vulnerable packages, direct or transitive'
    }
}

function Invoke-PackStage {
    New-Item -ItemType Directory -Path $feedDir -Force | Out-Null
    $pack = Invoke-Capture 'dotnet' @('pack', $solution, '-c', $Configuration, '-o', $feedDir, '--nologo', '-nodeReuse:false',
        "-p:VersionSuffix=$VersionSuffix")
    $nupkgs = @(Get-ChildItem -Path $feedDir -Filter '*.nupkg' -File)
    if ($pack.ExitCode -ne 0) { Add-Result 'Pack' 'FAIL' "dotnet pack exited $($pack.ExitCode)"; return }
    if ($nupkgs.Count -eq 0) { Add-Result 'Pack' 'FAIL' 'dotnet pack produced no packages'; return }

    $problems = [System.Collections.Generic.List[string]]::new()
    foreach ($nupkg in $nupkgs) {
        $meta = Get-PackageMetadata -Path $nupkg.FullName
        if (-not $meta) { $problems.Add("$($nupkg.Name): no .nuspec"); continue }
        if ($meta.Version -ne $expectedVersion) { $problems.Add("$($meta.Id): version $($meta.Version), expected $expectedVersion") }
        if ([string]::IsNullOrWhiteSpace($meta.License)) { $problems.Add("$($meta.Id): no <license> element") }
        if (-not $meta.Readme) { $problems.Add("$($meta.Id): no README.md"); continue }
        if ($meta.Readme.Contains('{{')) { $problems.Add("$($meta.Id): README.md has an unexpanded {{placeholder}}") }
        # The private host may appear only inside the configured repository URL, and only while it is Gitea.
        $rest = ($meta.Readme + $meta.Nuspec)
        if ($repositoryHost -eq 'Gitea') { $rest = $rest.Replace($repositoryUrl, '') }
        if ($rest -match 'mepeng') { $problems.Add("$($meta.Id): names the repository host outside InstellaRepositoryUrl") }
    }
    if ($problems.Count -gt 0) { Add-Result 'Pack' 'FAIL' ($problems -join '; '); return }

    # Packaged consumption. The in-tree sample uses ProjectReferences, so it
    # never exercises the packages. packageSourceMapping pins Instella.* to the
    # local feed so a stale nuget.org copy cannot satisfy the restore.
    $consumerRoot = Join-Path $tempRoot 'consumer'
    $payloadDir = Join-Path $consumerRoot 'PayloadApp'
    $installerDir = Join-Path $consumerRoot 'Verify.Installer'
    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
    New-Item -ItemType Directory -Path $installerDir -Force | Out-Null

    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="instella-local" value="$feedDir" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="instella-local"><package pattern="Instella.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -Path (Join-Path $consumerRoot 'NuGet.config') -Encoding UTF8

    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
'@ | Set-Content -Path (Join-Path $payloadDir 'PayloadApp.csproj') -Encoding UTF8
    'System.Console.WriteLine("payload app");' | Set-Content -Path (Join-Path $payloadDir 'Program.cs') -Encoding UTF8

    # TargetFramework is declared in the project body on purpose: the package's
    # build/*.props is imported above the body, which once broke defaults.
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>Verify.Installer</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Instella.Installer.Runtime" Version="$expectedVersion" />
    <PackageReference Include="Instella.Installer.Build" Version="$expectedVersion" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\PayloadApp\PayloadApp.csproj">
      <InstellaPayload>true</InstellaPayload>
      <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
      <Private>false</Private>
      <OutputItemType>_InstellaPayloadAssembly</OutputItemType>
      <SkipGetTargetFrameworkProperties>true</SkipGetTargetFrameworkProperties>
    </ProjectReference>
  </ItemGroup>
</Project>
"@ | Set-Content -Path (Join-Path $installerDir 'Verify.Installer.csproj') -Encoding UTF8

    @'
using Instella.Installer.Runtime.Builders;

return await InstellaInstaller.Create()
    .WithApp("VerifyApp", "com.instella.verify", new Version(1, 0, 0))
    .WithServer("https://example.invalid")
    .AllowUnsignedUpdates()
    .Build()
    .RunAsync(args);
'@ | Set-Content -Path (Join-Path $installerDir 'Program.cs') -Encoding UTF8

    if (-not $isWindowsHost) {
        Add-Result 'Pack' 'PASS' "$($nupkgs.Count) nupkg at $expectedVersion with a licence (consumer publish is Windows-only)"
        return
    }

    # A private packages folder: the version number does not change between commits, so
    # the user-wide NuGet cache would otherwise keep serving whatever was packed first.
    $consumerPackages = Join-Path $consumerRoot 'packages'
    $consume = Invoke-Capture 'dotnet' `
        @('publish', '-c', $Configuration, '-r', 'win-x64', '-p:PublishAot=false', '-nodeReuse:false',
          "-p:RestorePackagesPath=$consumerPackages") `
        -WorkingDirectory $installerDir
    $consumedExe = Join-Path $installerDir "bin\$Configuration\net10.0\win-x64\publish\Verify.Installer.exe"

    if ($consume.ExitCode -ne 0) { Add-Result 'Pack' 'FAIL' "consumer publish from the local feed exited $($consume.ExitCode)"; return }
    if (-not (Test-Path $consumedExe)) { Add-Result 'Pack' 'FAIL' 'consumer publish produced no installer exe'; return }
    if (-not (Test-FooterMagic -Path $consumedExe)) { Add-Result 'Pack' 'FAIL' 'consumer installer carries no INSTELLA footer'; return }
    $entries = Get-PayloadEntries -Path $consumedExe
    if ($entries -notcontains '.instella/instella.exe') { Add-Result 'Pack' 'FAIL' 'the payload carries no stub (.instella/instella.exe)'; return }

    # Build pipeline robustness. A stale file in the payload
    # publish directory must not survive, and a republish must give identical bytes.
    $firstHash = (Get-FileHash $consumedExe -Algorithm SHA256).Hash
    $stale = Join-Path $installerDir "obj\$Configuration\net10.0\win-x64\instella-payload\PayloadApp\stale-from-earlier.txt"
    Set-Content -Path $stale -Value 'stale'
    $republish = Invoke-Capture 'dotnet' `
        @('publish', '-c', $Configuration, '-r', 'win-x64', '-p:PublishAot=false', '-nodeReuse:false',
          "-p:RestorePackagesPath=$consumerPackages") `
        -WorkingDirectory $installerDir
    if ($republish.ExitCode -ne 0) { Add-Result 'Pack' 'FAIL' "consumer republish exited $($republish.ExitCode)"; return }
    if ((Get-PayloadEntries -Path $consumedExe) -contains 'stale-from-earlier.txt') { Add-Result 'Pack' 'FAIL' 'a stale payload-publish file reached the payload'; return }
    if ((Get-FileHash $consumedExe -Algorithm SHA256).Hash -ne $firstHash) { Add-Result 'Pack' 'FAIL' 'republishing produced a different installer'; return }

    # Another architecture than the build machine: the manifest comes from a host build.
    $arm = Invoke-Capture 'dotnet' `
        @('publish', '-c', $Configuration, '-r', 'win-arm64', '-p:PublishAot=false', '-nodeReuse:false',
          "-p:RestorePackagesPath=$consumerPackages") `
        -WorkingDirectory $installerDir
    $armExe = Join-Path $installerDir "bin\$Configuration\net10.0\win-arm64\publish\Verify.Installer.exe"
    if ($arm.ExitCode -ne 0 -or -not (Test-FooterMagic -Path $armExe)) { Add-Result 'Pack' 'FAIL' 'publishing win-arm64 did not produce an installer'; return }

    # Another OS: works from this host, and says the platform is experimental.
    $linux = Invoke-Capture 'dotnet' `
        @('publish', '-c', $Configuration, '-r', 'linux-x64', '-p:PublishAot=false', '-nodeReuse:false',
          "-p:RestorePackagesPath=$consumerPackages") `
        -WorkingDirectory $installerDir
    $linuxInstaller = Join-Path $installerDir "bin\$Configuration\net10.0\linux-x64\publish\Verify.Installer"
    if ($linux.ExitCode -ne 0 -or -not (Test-FooterMagic -Path $linuxInstaller)) { Add-Result 'Pack' 'FAIL' 'publishing linux-x64 did not produce an installer'; return }
    if ($linux.Output -notmatch 'INSTELLA0001') { Add-Result 'Pack' 'FAIL' 'a linux-x64 publish did not warn INSTELLA0001 (experimental platform)'; return }

    # Instella dictates the payload directory, so custom output layouts work.
    $artifacts = Join-Path $consumerRoot 'artifacts'
    $art = Invoke-Capture 'dotnet' `
        @('publish', '-c', $Configuration, '-r', 'win-x64', '-p:PublishAot=false', '-nodeReuse:false',
          "-p:RestorePackagesPath=$consumerPackages", '-p:UseArtifactsOutput=true', "-p:ArtifactsPath=$artifacts") `
        -WorkingDirectory $installerDir
    $artExe = Join-Path $artifacts "publish\Verify.Installer\$($Configuration.ToLowerInvariant())_win-x64\Verify.Installer.exe"
    if ($art.ExitCode -ne 0 -or -not (Test-FooterMagic -Path $artExe)) { Add-Result 'Pack' 'FAIL' 'publishing with UseArtifactsOutput did not produce an installer'; return }

    # The init template publishes: the CLI from the local feed scaffolds "Quick Notes" for an
    # ordinary framework-dependent app, and the self-contained Release publish succeeds. A
    # self-contained installer referencing a framework-dependent exe fails with NETSDK1150
    # unless the template turns that validation off.
    $initRoot = Join-Path $tempRoot 'init'
    $toolDir = Join-Path $initRoot 'tools'
    New-Item -ItemType Directory -Path (Join-Path $initRoot 'QuickNotes') -Force | Out-Null
    # The CLI's package id is instella-cli, which the Instella.* mapping does not cover.
    (Get-Content (Join-Path $consumerRoot 'NuGet.config') -Raw).Replace(
        '<package pattern="Instella.*" />', '<package pattern="Instella.*" /><package pattern="instella-cli" />') |
        Set-Content -Path (Join-Path $initRoot 'NuGet.config') -Encoding UTF8
    Copy-Item (Join-Path $payloadDir 'PayloadApp.csproj') (Join-Path $initRoot 'QuickNotes\QuickNotes.csproj')
    Copy-Item (Join-Path $payloadDir 'Program.cs') (Join-Path $initRoot 'QuickNotes\Program.cs')
    $tool = Invoke-Capture 'dotnet' @('tool', 'install', 'instella-cli', '--tool-path', $toolDir, '--version', $expectedVersion,
        '--configfile', (Join-Path $initRoot 'NuGet.config'))
    if ($tool.ExitCode -ne 0) { Add-Result 'Pack' 'FAIL' "installing the CLI from the local feed exited $($tool.ExitCode)"; return }
    $instella = Join-Path $toolDir 'instella.exe'
    $keys = Invoke-Capture $instella @('keys', 'generate', '--out', (Join-Path $initRoot 'throwaway.pem'))
    $publicKey = ([regex]::Match($keys.Output, 'Public key:\s*(\S+)')).Groups[1].Value
    if ($keys.ExitCode -ne 0 -or -not $publicKey) { Add-Result 'Pack' 'FAIL' 'instella keys generate printed no public key'; return }
    $init = Invoke-Capture $instella @('init', '--name', 'Quick Notes', '--app', (Join-Path $initRoot 'QuickNotes\QuickNotes.csproj'),
        '--publisher-key', $publicKey, '--output', $initRoot) -WorkingDirectory $initRoot
    if ($init.ExitCode -ne 0) { Add-Result 'Pack' 'FAIL' "instella init exited $($init.ExitCode)"; return }
    if ($init.Output -notmatch 'cd QuickNotes\.Installer') { Add-Result 'Pack' 'FAIL' 'instella init printed the wrong project folder'; return }
    $initPublish = Invoke-Capture 'dotnet' `
        @('publish', '-c', 'Release', '-r', 'win-x64', '-p:PublishAot=false', '-nodeReuse:false',
          "-p:RestorePackagesPath=$consumerPackages") `
        -WorkingDirectory (Join-Path $initRoot 'QuickNotes.Installer')
    $initExe = Join-Path $initRoot 'QuickNotes.Installer\bin\Release\net10.0\win-x64\publish\QuickNotes.Installer.exe'
    if ($initPublish.ExitCode -ne 0) { Add-Result 'Pack' 'FAIL' "publishing the init template exited $($initPublish.ExitCode)"; return }
    if (-not (Test-FooterMagic -Path $initExe)) { Add-Result 'Pack' 'FAIL' 'the init template published no installer with a footer'; return }

    Add-Result 'Pack' 'PASS' "$($nupkgs.Count) nupkg at $expectedVersion; consumer installer is reproducible, stub-carrying, and builds for win-arm64, linux-x64 and artifacts output; the init template publishes"
}

# The newest signtool.exe from the Windows 10/11 SDK, or $null.
function Find-SignTool {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path $kits)) { return $null }
    $tool = Get-ChildItem -Path $kits -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } |
        Sort-Object { $_.Directory.Parent.Name } -Descending |
        Select-Object -First 1
    if ($tool) { return $tool.FullName } else { return $null }
}

# True when $Path carries an intact Authenticode signature by $Thumbprint. A throwaway
# self-signed certificate is not trusted, so the status may be UnknownError/NotTrusted;
# what must hold is that the signature exists, is ours, and its hash matches the file.
function Test-SignedBy {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Thumbprint)
    $sig = Get-AuthenticodeSignature -FilePath $Path
    return $sig.SignerCertificate -and $sig.SignerCertificate.Thumbprint -eq $Thumbprint `
        -and $sig.Status -notin @('NotSigned', 'HashMismatch', 'NotSupportedFileFormat')
}

# Signed installers: the Pack stage's consumer is published with
# InstellaSignCommand and a throwaway certificate. The installer must still pass its own
# payload integrity check, and the stub it installs must carry an intact signature.
function Invoke-SigningStage {
    if (-not $isWindowsHost) { Add-Result 'Signing' 'SKIP' 'Windows-only stage'; return }
    $signtool = Find-SignTool
    if (-not $signtool) { Add-Result 'Signing' 'SKIP' 'missing prerequisite: Windows SDK signtool.exe'; return }

    $signedDir = Join-Path $tempRoot 'signed-publish'
    $exe = Join-Path $signedDir 'Verify.Installer.exe'
    $installDir = Join-Path $tempRoot 'signing-install'
    $installerDir = Join-Path $tempRoot 'consumer\Verify.Installer'

    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Instella verify (throwaway)' `
        -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddDays(1)
    # A second signer, for the handoff check's real-API test (the subjects must differ).
    $certB = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Instella verify B (throwaway)' `
        -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddDays(1)
    try {
        # WinVerifyTrust through Authenticode.Read, with the same and with different signers.
        $env:INSTELLA_SIGNTOOL = $signtool
        $env:INSTELLA_SIGN_CERT_A = $cert.Thumbprint
        $env:INSTELLA_SIGN_CERT_B = $certB.Thumbprint
        $auth = Invoke-Capture 'dotnet' @('test', (Join-Path $repoRoot 'tests\Instella.Installer.Runtime.Tests'), '-c', $Configuration,
            '-nodeReuse:false', '--filter', 'TestCategory=Signing')
        Remove-Item Env:\INSTELLA_SIGNTOOL, Env:\INSTELLA_SIGN_CERT_A, Env:\INSTELLA_SIGN_CERT_B -ErrorAction SilentlyContinue
        $m = [regex]::Match($auth.Output, 'Failed:\s+(\d+),\s+Passed:\s+(\d+)')
        if ($auth.ExitCode -ne 0 -or -not $m.Success -or [int]$m.Groups[1].Value -gt 0 -or [int]$m.Groups[2].Value -eq 0) {
            Add-Result 'Signing' 'FAIL' 'the Authenticode reader tests (TestCategory=Signing) failed or did not run'
            return
        }

        if (-not (Test-Path (Join-Path $installerDir 'Verify.Installer.csproj'))) {
            Add-Result 'Signing' 'SKIP' 'needs the Pack stage in the same run (it creates the installer project this stage publishes)'
            return
        }

        # MSBuild reads environment variables as properties; this avoids quoting the command
        # through the dotnet command line.
        $env:InstellaSignCommand = "`"$signtool`" sign /fd SHA256 /sha1 $($cert.Thumbprint) `"{0}`""
        $publish = Invoke-Capture 'dotnet' `
            @('publish', '-c', $Configuration, '-r', 'win-x64', '-p:PublishAot=false', '-nodeReuse:false',
              "-p:RestorePackagesPath=$(Join-Path $tempRoot 'consumer\packages')", "-p:PublishDir=$signedDir\") `
            -WorkingDirectory $installerDir
        Remove-Item Env:\InstellaSignCommand -ErrorAction SilentlyContinue
        if ($publish.ExitCode -ne 0) { Add-Result 'Signing' 'FAIL' "signed publish exited $($publish.ExitCode)"; return }
        if (-not (Test-SignedBy -Path $exe -Thumbprint $cert.Thumbprint)) { Add-Result 'Signing' 'FAIL' 'the published installer is not signed'; return }

        $install = Start-Process -FilePath $exe -ArgumentList @('--silent', '--scope', 'user', '--path', "`"$installDir`"") `
            -WorkingDirectory $signedDir -NoNewWindow -Wait -PassThru
        if ($install.ExitCode -ne 0) {
            Add-Result 'Signing' 'FAIL' "the signed installer exited $($install.ExitCode) (12 = it rejected its own payload)"
            return
        }
        $stub = Join-Path $installDir 'instella.exe'
        $stubSigned = (Test-Path $stub) -and (Test-SignedBy -Path $stub -Thumbprint $cert.Thumbprint)
        Start-Process -FilePath $exe -ArgumentList @('--uninstall', '--silent', '--path', "`"$installDir`"") `
            -WorkingDirectory $signedDir -NoNewWindow -Wait | Out-Null
        if (-not $stubSigned) { Add-Result 'Signing' 'FAIL' 'the installed instella.exe does not carry an intact signature'; return }
        Add-Result 'Signing' 'PASS' 'Authenticode reader; signed installer verified its payload, installed --silent, and staged a signed stub'
    }
    finally {
        Remove-Item Env:\InstellaSignCommand, Env:\INSTELLA_SIGNTOOL, Env:\INSTELLA_SIGN_CERT_A, Env:\INSTELLA_SIGN_CERT_B -ErrorAction SilentlyContinue
        Remove-Item -Path $cert.PSPath -Force -ErrorAction SilentlyContinue
        Remove-Item -Path $certB.PSPath -Force -ErrorAction SilentlyContinue
    }
}

# Runs the tests of one category from the E2E project; returns (passed, failed, skipped, exit).
function Invoke-CategoryTests {
    param([Parameter(Mandatory)][string]$Category)
    $run = Invoke-Capture 'dotnet' @('test', (Join-Path $repoRoot 'tests\Instella.E2E.Tests'), '-c', $Configuration,
        '-nodeReuse:false', '--filter', "TestCategory=$Category")
    $m = [regex]::Match($run.Output, 'Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+)')
    if (-not $m.Success) { return [pscustomobject]@{ Passed = 0; Failed = 0; Skipped = 0; Exit = $run.ExitCode; Parsed = $false } }
    return [pscustomobject]@{
        Passed = [int]$m.Groups[2].Value; Failed = [int]$m.Groups[1].Value; Skipped = [int]$m.Groups[3].Value
        Exit = $run.ExitCode; Parsed = $true
    }
}

# The end-to-end lifecycle.
function Invoke-E2EStage {
    if (-not $isWindowsHost) { Add-Result 'E2E' 'SKIP' 'Windows-only stage'; return }
    $r = Invoke-CategoryTests -Category 'E2E'
    if (-not $r.Parsed) { Add-Result 'E2E' 'FAIL' 'could not parse the E2E test summary' }
    elseif ($r.Exit -ne 0 -or $r.Failed -gt 0) { Add-Result 'E2E' 'FAIL' "$($r.Failed) failed" }
    elseif ($r.Passed -eq 0) { Add-Result 'E2E' 'FAIL' 'the E2E test did not run' }
    else { Add-Result 'E2E' 'PASS' 'install, update, crash recovery, tamper refusal, uninstall and leak check' }
}

# The server image with empty volumes.
function Invoke-DockerStage {
    if (-not (Get-Command 'docker' -ErrorAction SilentlyContinue)) { Add-Result 'Docker' 'SKIP' 'missing prerequisite: Docker'; return }
    $r = Invoke-CategoryTests -Category 'Docker'
    if (-not $r.Parsed) { Add-Result 'Docker' 'FAIL' 'could not parse the Docker test summary' }
    elseif ($r.Exit -ne 0 -or $r.Failed -gt 0) { Add-Result 'Docker' 'FAIL' "$($r.Failed) failed" }
    elseif ($r.Passed -eq 0) { Add-Result 'Docker' 'SKIP' 'the Docker daemon is not available' }
    else { Add-Result 'Docker' 'PASS' 'image starts empty, keeps state in volumes, survives a restart' }
}

function Invoke-AotStage {
    if (-not $isWindowsHost) { Add-Result 'Aot' 'SKIP' 'Windows-only stage'; return }
    $vswhere = Find-VsWhere
    if (-not $vswhere) {
        Add-Result 'Aot' 'SKIP' 'missing prerequisite: VS "Desktop development with C++" (vswhere.exe / MSVC linker)'
        return
    }
    $samplePath = Join-Path $repoRoot 'samples\SampleApp.Installer'
    # Switching PublishAot without wiping bin/obj reuses the previous non-AOT
    # publish directory. Clean first, always.
    foreach ($proj in @($samplePath, (Join-Path $repoRoot 'samples\SampleApp'))) {
        foreach ($sub in @('bin', 'obj')) {
            $target = Join-Path $proj $sub
            if (Test-Path $target) { Remove-Item -Recurse -Force $target }
        }
    }
    $vsInstallerDir = Split-Path -Parent $vswhere
    if (($env:PATH -split ';') -notcontains $vsInstallerDir) { $env:PATH = "$vsInstallerDir;$env:PATH" }

    $aot = Invoke-Capture 'dotnet' @('publish', '-r', 'win-x64', '-c', $Configuration, '-nodeReuse:false') -WorkingDirectory $samplePath
    $aotExe = Join-Path $samplePath "bin\$Configuration\net10.0\win-x64\publish\QuickNotes.Installer.exe"

    if ($aot.ExitCode -ne 0) { Add-Result 'Aot' 'FAIL' "AOT publish exited $($aot.ExitCode)"; return }
    if ($aot.Output -match 'warning IL\d{4}') { Add-Result 'Aot' 'FAIL' 'AOT/trim warnings emitted (see output above)'; return }
    if (-not (Test-Path $aotExe)) { Add-Result 'Aot' 'FAIL' 'AOT publish produced no exe'; return }
    if (-not (Test-FooterMagic -Path $aotExe)) { Add-Result 'Aot' 'FAIL' 'AOT installer carries no INSTELLA footer'; return }

    # Run the exe ALONE in an empty directory: an apphost would fail to find
    # its managed DLLs there, a native binary will not.
    $isolated = Join-Path $tempRoot 'aot-isolated'
    New-Item -ItemType Directory -Path $isolated -Force | Out-Null
    Copy-Item $aotExe $isolated
    $proc = Start-Process -FilePath (Join-Path $isolated 'QuickNotes.Installer.exe') `
        -ArgumentList '--help' -WorkingDirectory $isolated -NoNewWindow -Wait -PassThru
    if ($proc.ExitCode -eq 0) { Add-Result 'Aot' 'PASS' 'standalone native exe, --help exits 0 in isolation' }
    else { Add-Result 'Aot' 'FAIL' "isolated --help exited $($proc.ExitCode) - not a native binary" }
}

New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
Write-Host 'Instella verification run' -ForegroundColor Cyan
Write-Host "  repo:    $repoRoot"
Write-Host "  stages:  $($selected -join ', ')"
Write-Host "  scratch: $tempRoot"

try {
    foreach ($name in $selected) {
        Write-Stage $name
        & "Invoke-${name}Stage"
        if ($results[-1].Status -eq 'FAIL') { break }
    }
}
catch {
    Add-Result 'Run' 'FAIL' "unhandled error: $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkRed
}
finally {
    if ($KeepTemp) { Write-Host "Scratch directory retained at $tempRoot" -ForegroundColor Yellow }
    elseif (Test-Path $tempRoot) { Remove-Item -Recurse -Force $tempRoot -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host '=== Summary ===' -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-String | Write-Host

$failedStages = @($results | Where-Object { $_.Status -eq 'FAIL' })
$skippedStages = @($results | Where-Object { $_.Status -eq 'SKIP' })
if ($failedStages.Count -gt 0) {
    Write-Host "VERIFICATION FAILED - $(($failedStages.Stage) -join ', ')" -ForegroundColor Red
    exit 1
}
if ($skippedStages.Count -gt 0 -and $NoSkip) {
    Write-Host "VERIFICATION FAILED - skipped with -NoSkip: $(($skippedStages.Stage) -join ', ')" -ForegroundColor Red
    exit 1
}
if ($skippedStages.Count -gt 0) {
    Write-Host "Verification passed, with stage(s) skipped: $(($skippedStages.Stage) -join ', ')" -ForegroundColor Yellow
}
else {
    Write-Host 'Verification passed - all selected stages green.' -ForegroundColor Green
}
exit 0

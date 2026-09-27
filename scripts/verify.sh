#!/usr/bin/env bash
#
# Local verification run for the Instella repository (Linux/macOS).
#
# This is the portable SUBSET of scripts/verify.ps1, which is the primary and
# authoritative gate. It is a quick local check on Linux/macOS; `pwsh -Command
# "./scripts/verify.ps1 -Stage Build,Test,Audit,Migrations"` runs the full
# Linux-capable stages.
#
# Stages here: build, test, pack, packaged consumption.
#
# The AOT, Signing and E2E stages are Windows-only and are not reproduced here.
# This script publishes the host's own RID only; cross-RID publishing is
# covered by verify.ps1's Pack stage (win-arm64 and linux-x64 from Windows).
#
set -euo pipefail

CONFIGURATION="${CONFIGURATION:-Release}"
EXPECTED_PACKAGES="${EXPECTED_PACKAGES:-6}"
EXPECTED_SYMBOL_PACKAGES="${EXPECTED_SYMBOL_PACKAGES:-4}"

# Deliberately NOT defaulted to the Windows numbers (660/1). The suite is
# platform-conditional in both directions -- roughly 38 tests sit behind
# [Platform("Win")] and 6 behind [Platform("Linux")]/[Platform("MacOsX")], and NUnit
# drops platform-excluded tests from the VSTest summary entirely -- so no single
# total is correct everywhere. Unset, stage 2 asserts what is true on every
# platform: nothing failed and tests actually ran. Set EXPECTED_PASSED (and
# optionally EXPECTED_SKIPPED) to pin exact counts for one known machine.
EXPECTED_PASSED="${EXPECTED_PASSED:-}"
EXPECTED_SKIPPED="${EXPECTED_SKIPPED:-}"

# Retain the scratch directory (and the build/test logs inside it) for inspection.
# Failing runs retain it automatically -- see the summary block.
KEEP_TEMP="${KEEP_TEMP:-0}"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
solution="$repo_root/Instella.sln"
temp_root="$(mktemp -d "${TMPDIR:-/tmp}/instella-verify-XXXXXXXX")"
feed_dir="$temp_root/feed"

cleanup() {
    if [ "$KEEP_TEMP" = '1' ]; then
        printf '\nScratch directory retained at %s\n' "$temp_root"
    else
        rm -rf "$temp_root"
    fi
}
trap cleanup EXIT

stage_names=()
stage_status=()
stage_detail=()

add_result() {
    stage_names+=("$1")
    stage_status+=("$2")
    stage_detail+=("$3")
    printf '  [%s] %s %s\n' "$2" "$1" "$3"
}

write_stage() {
    printf '\n=== %s ===\n' "$1"
}

# This script IS this repository's CI, so it should build the way a CI would.
# Directory.Build.props gates ContinuousIntegrationBuild on $(CI); without it the
# path-normalisation half of the SourceLink/determinism setup never runs and every
# shipped PDB embeds the maintainer's absolute worktree path. Set it for the whole
# run so what gets tested is what gets packed.
export CI=true

printf 'Instella verification run\n'
printf '  repo:    %s\n' "$repo_root"
printf '  scratch: %s\n' "$temp_root"

# ----------------------------------------------------------------------
# Stage 1 - Build
#
# -nodeReuse:false is required, not cosmetic: persistent MSBuild nodes hold a
# lock on Instella.Installer.Build.dll and its copy of Instella.Core.dll, and
# the next build in the same session fails MSB3027/MSB3021.
# ----------------------------------------------------------------------
write_stage 'Stage 1: build'
build_log="$temp_root/build.log"
if dotnet build "$solution" -c "$CONFIGURATION" -nodeReuse:false 2>&1 | tee "$build_log"; then
    # Anchor the count. A bare '0 Warning(s)' substring also matches '10 Warning(s)',
    # so a burst of new warnings -- exactly what an analyser wave or SDK bump
    # produces -- would be reported as clean.
    warning_count="$(grep -oE '^[[:space:]]*[0-9]+ Warning\(s\)' "$build_log" | grep -oE '[0-9]+' | tail -n 1)"
    if [ -z "$warning_count" ]; then
        add_result 'Build' 'FAIL' 'could not find a warning count in the build output'
    elif [ "$warning_count" -ne 0 ]; then
        add_result 'Build' 'FAIL' "build succeeded but emitted $warning_count warning(s)"
    else
        add_result 'Build' 'PASS' '0 warnings, 0 errors'
    fi
else
    add_result 'Build' 'FAIL' 'dotnet build failed'
fi

# ----------------------------------------------------------------------
# Stage 2 - Test
# ----------------------------------------------------------------------
write_stage 'Stage 2: test'
test_log="$temp_root/test.log"
test_exit=0
dotnet test "$solution" -c "$CONFIGURATION" --no-build 2>&1 | tee "$test_log" || test_exit=$?

# Sum the per-assembly summary lines rather than trusting a single total.
read -r t_failed t_passed t_skipped t_assemblies <<<"$(
    grep -oE 'Failed:[[:space:]]+[0-9]+,[[:space:]]+Passed:[[:space:]]+[0-9]+,[[:space:]]+Skipped:[[:space:]]+[0-9]+' "$test_log" |
        grep -oE '[0-9]+' |
        awk 'NR%3==1{f+=$1} NR%3==2{p+=$1} NR%3==0{s+=$1; n+=1} END{printf "%d %d %d %d", f, p, s, n}'
)"

counts="$t_passed passed, $t_failed failed, $t_skipped skipped across $t_assemblies assemblies"
if [ "$t_assemblies" -eq 0 ]; then
    add_result 'Test' 'FAIL' 'could not parse any test summary - did any tests run?'
elif [ "$test_exit" -ne 0 ] || [ "$t_failed" -gt 0 ]; then
    add_result 'Test' 'FAIL' "$counts"
elif [ "$t_passed" -eq 0 ]; then
    add_result 'Test' 'FAIL' "$counts - no tests passed, which means none ran"
elif [ -n "$EXPECTED_PASSED" ] && [ "$t_passed" -ne "$EXPECTED_PASSED" ]; then
    add_result 'Test' 'FAIL' "$counts - expected $EXPECTED_PASSED passed"
elif [ -n "$EXPECTED_SKIPPED" ] && [ "$t_skipped" -ne "$EXPECTED_SKIPPED" ]; then
    add_result 'Test' 'FAIL' "$counts - expected $EXPECTED_SKIPPED skipped"
else
    add_result 'Test' 'PASS' "$counts"
fi

# ----------------------------------------------------------------------
# Stage 3 - Pack
# ----------------------------------------------------------------------
write_stage 'Stage 3: pack'
mkdir -p "$feed_dir"
expected_version="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$repo_root/Directory.Build.props" | head -n 1)"

if dotnet pack "$solution" -c "$CONFIGURATION" -o "$feed_dir" --nologo; then
    nupkgs=$(find "$feed_dir" -maxdepth 1 -name '*.nupkg' | wc -l | tr -d ' ')
    snupkgs=$(find "$feed_dir" -maxdepth 1 -name '*.snupkg' | wc -l | tr -d ' ')
    if [ "$nupkgs" -ne "$EXPECTED_PACKAGES" ]; then
        add_result 'Pack' 'FAIL' "expected $EXPECTED_PACKAGES nupkg, got $nupkgs"
    elif [ "$snupkgs" -ne "$EXPECTED_SYMBOL_PACKAGES" ]; then
        add_result 'Pack' 'FAIL' "expected $EXPECTED_SYMBOL_PACKAGES snupkg, got $snupkgs"
    elif ! command -v unzip >/dev/null 2>&1; then
        add_result 'Pack' 'PASS' "$nupkgs nupkg + $snupkgs snupkg (contents unverified: unzip not installed)"
    else
        # Counting files is not enough: dropping PackageLicenseExpression, or
        # letting a project reintroduce its own <Version>, produces exactly the
        # right number of files. Licence and version are what nuget.org consumers
        # rely on, so assert them for real.
        pack_problems=''
        while IFS= read -r pkg; do
            nuspec="$(unzip -p "$pkg" '*.nuspec' 2>/dev/null | tr -d '\r')"
            pkg_id="$(printf '%s' "$nuspec" | sed -n 's:.*<id>\(.*\)</id>.*:\1:p' | head -n 1)"
            pkg_version="$(printf '%s' "$nuspec" | sed -n 's:.*<version>\(.*\)</version>.*:\1:p' | head -n 1)"
            if [ "$pkg_version" != "$expected_version" ]; then
                pack_problems="$pack_problems; $pkg_id: version $pkg_version, expected $expected_version"
            fi
            if ! printf '%s' "$nuspec" | grep -q '<license[ >]'; then
                pack_problems="$pack_problems; $pkg_id: no <license> element"
            fi
        done <<<"$(find "$feed_dir" -maxdepth 1 -name '*.nupkg')"

        if [ -n "$pack_problems" ]; then
            add_result 'Pack' 'FAIL' "${pack_problems#; }"
        else
            add_result 'Pack' 'PASS' "$nupkgs nupkg + $snupkgs snupkg, all $expected_version with a licence"
        fi
    fi
else
    add_result 'Pack' 'FAIL' 'dotnet pack failed'
fi

# ----------------------------------------------------------------------
# Stage 4 - Packaged consumption
#
# The in-tree sample cannot verify this: it uses ProjectReferences and imports
# the targets by relative path, so it never exercises the package. Generate a
# throwaway consumer that references the packages properly.
#
# packageSourceMapping pins every Instella.* package to the local folder feed,
# so a stale copy on a remote registry could not satisfy the restore, while
# third-party dependencies still resolve normally.
# ----------------------------------------------------------------------
write_stage 'Stage 4: packaged consumption'
consumer_root="$temp_root/consumer"
payload_dir="$consumer_root/PayloadApp"
installer_dir="$consumer_root/Verify.Installer"
mkdir -p "$payload_dir" "$installer_dir"

version="$expected_version"
rid="$(dotnet --info | sed -n 's/^[[:space:]]*RID:[[:space:]]*//p' | head -n 1)"

cat >"$consumer_root/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="instella-local" value="$feed_dir" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="instella-local">
      <package pattern="Instella.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF

cat >"$payload_dir/PayloadApp.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
EOF

echo 'System.Console.WriteLine("payload app");' >"$payload_dir/Program.cs"

# TargetFramework is declared in the project body on purpose. That is the normal
# thing to do, and it is the case that breaks easily: a package's build/*.props is
# imported above the body, so any default derived from $(TargetFramework) there
# evaluates to empty. Keep it here so that case stays covered.
cat >"$installer_dir/Verify.Installer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>Verify.Installer</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Instella.Installer.Runtime" Version="$version" />
    <PackageReference Include="Instella.Installer.Build" Version="$version" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../PayloadApp/PayloadApp.csproj">
      <InstellaPayload>true</InstellaPayload>
      <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
      <Private>false</Private>
      <OutputItemType>_InstellaPayloadAssembly</OutputItemType>
      <SkipGetTargetFrameworkProperties>true</SkipGetTargetFrameworkProperties>
    </ProjectReference>
  </ItemGroup>
</Project>
EOF

cat >"$installer_dir/Program.cs" <<'EOF'
using Instella.Installer.Runtime.Builders;

return await InstellaInstaller.Create()
    .WithApp("VerifyApp", "com.instella.verify", new Version(1, 0, 0))
    .WithServer("https://example.invalid")
    .AllowUnsignedUpdates()
    .Build()
    .RunAsync(args);
EOF

# Non-AOT keeps this stage fast. It still proves what matters: the package
# resolves, MSBuild loads the task from tasks/<tfm>/ with its dependencies
# beside it, and the payload is appended.
if (cd "$installer_dir" && dotnet publish -c "$CONFIGURATION" -r "$rid" -p:PublishAot=false -nodeReuse:false); then
    # No .exe extension off Windows: the targets derive it from the build host.
    consumed_exe="$installer_dir/bin/$CONFIGURATION/net10.0/$rid/publish/Verify.Installer"
    if [ ! -f "$consumed_exe" ]; then
        add_result 'Consume' 'FAIL' 'publish produced no installer binary'
    elif [ "$(tail -c 8 "$consumed_exe")" != 'INSTELLA' ]; then
        add_result 'Consume' 'FAIL' 'installer carries no INSTELLA footer magic - payload was not appended'
    else
        add_result 'Consume' 'PASS' 'restored from local feed, payload appended, footer magic present'
    fi
else
    add_result 'Consume' 'FAIL' 'publish from the local feed failed'
fi

add_result 'AOT' 'SKIP' 'Windows-only stage; use scripts/verify.ps1'

# ----------------------------------------------------------------------
# Summary
# ----------------------------------------------------------------------
printf '\n=== Summary ===\n'
failures=0
skips=0
for i in "${!stage_names[@]}"; do
    printf '  %-10s %-5s %s\n' "${stage_names[$i]}" "${stage_status[$i]}" "${stage_detail[$i]}"
    [ "${stage_status[$i]}" = 'FAIL' ] && failures=$((failures + 1))
    [ "${stage_status[$i]}" = 'SKIP' ] && skips=$((skips + 1))
done
printf '\n'

if [ "$failures" -gt 0 ]; then
    # Keep the evidence on exactly the runs where it is needed: build.log and
    # test.log live in the scratch directory, and a failing run is useless
    # without them.
    KEEP_TEMP=1
    printf 'VERIFICATION FAILED - %d stage(s).\n' "$failures"
    exit 1
fi

if [ "$skips" -gt 0 ]; then
    printf 'Verification passed, with %d stage(s) skipped.\n' "$skips"
else
    printf 'Verification passed - all stages green.\n'
fi
exit 0

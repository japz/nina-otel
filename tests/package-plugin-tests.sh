#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/nina-otel-package-test.XXXXXX")"
trap 'rm -rf "$work_dir"' EXIT

list_archive_entries() {
  local archive_path="$1"
  if command -v unzip >/dev/null 2>&1; then
    unzip -Z1 "$archive_path"
    return
  fi
  if command -v zipinfo >/dev/null 2>&1; then
    zipinfo -1 "$archive_path"
    return
  fi
  if command -v pwsh >/dev/null 2>&1 || command -v powershell >/dev/null 2>&1; then
    local ps_archive_path="$archive_path"
    if command -v cygpath >/dev/null 2>&1; then
      ps_archive_path="$(cygpath -w "$archive_path")"
    fi
    local ps_bin="pwsh"
    if ! command -v pwsh >/dev/null 2>&1; then
      ps_bin="powershell"
    fi
    ZIP_PATH="$ps_archive_path" "$ps_bin" -NoProfile -Command \
      '$ErrorActionPreference = "Stop"; Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::OpenRead($env:ZIP_PATH).Entries | ForEach-Object { $_.FullName }'
    return
  fi

  printf 'No supported zip listing tool is available.\n' >&2
  return 1
}

build_output="$work_dir/build-output"
zip_path="$work_dir/NinaOtel.Plugin.zip"
mkdir -p "$build_output/runtimes/win-x64/native" "$build_output/en-US"

touch \
  "$build_output/NinaOtel.Plugin.dll" \
  "$build_output/NinaOtel.Plugin.pdb" \
  "$build_output/NinaOtel.Plugin.deps.json" \
  "$build_output/NinaOtel.Plugin.runtimeconfig.json" \
  "$build_output/NinaOtel.Core.dll" \
  "$build_output/NinaOtel.Abstractions.dll" \
  "$build_output/NinaOtel.Addons.NightSummary.dll" \
  "$build_output/NinaOtel.Addons.OnStepX.dll" \
  "$build_output/NinaOtel.Addons.PHD2.dll" \
  "$build_output/NinaOtel.Addons.TargetScheduler.dll" \
  "$build_output/NINA.Core.dll" \
  "$build_output/NINA.Plugin.dll" \
  "$build_output/OpenTelemetry.dll" \
  "$build_output/OpenTelemetry.Api.dll" \
  "$build_output/OpenTelemetry.Api.ProviderBuilderExtensions.dll" \
  "$build_output/OpenTelemetry.Exporter.OpenTelemetryProtocol.dll" \
  "$build_output/Microsoft.Extensions.Configuration.dll" \
  "$build_output/Microsoft.Extensions.Configuration.Abstractions.dll" \
  "$build_output/Microsoft.Extensions.Configuration.Binder.dll" \
  "$build_output/Microsoft.Extensions.DependencyInjection.dll" \
  "$build_output/Microsoft.Extensions.DependencyInjection.Abstractions.dll" \
  "$build_output/Microsoft.Extensions.Diagnostics.Abstractions.dll" \
  "$build_output/Microsoft.Extensions.Logging.dll" \
  "$build_output/Microsoft.Extensions.Logging.Abstractions.dll" \
  "$build_output/Microsoft.Extensions.Logging.Configuration.dll" \
  "$build_output/Microsoft.Extensions.Options.dll" \
  "$build_output/Microsoft.Extensions.Options.ConfigurationExtensions.dll" \
  "$build_output/Microsoft.Extensions.Primitives.dll" \
  "$build_output/Microsoft.Bcl.AsyncInterfaces.dll" \
  "$build_output/System.Diagnostics.DiagnosticSource.dll" \
  "$build_output/System.Security.Cryptography.ProtectedData.dll" \
  "$build_output/Google.Protobuf.dll" \
  "$build_output/en-US/NINA.Core.resources.dll" \
  "$build_output/runtimes/win-x64/native/SQLite.Interop.dll"

"$repo_root/scripts/package-plugin.sh" "$build_output" "$zip_path"

archive_entries="$(list_archive_entries "$zip_path" | sort)"
expected_entries="$(cat <<'ENTRIES'
Microsoft.Bcl.AsyncInterfaces.dll
Microsoft.Extensions.Configuration.Abstractions.dll
Microsoft.Extensions.Configuration.Binder.dll
Microsoft.Extensions.Configuration.dll
Microsoft.Extensions.DependencyInjection.Abstractions.dll
Microsoft.Extensions.DependencyInjection.dll
Microsoft.Extensions.Diagnostics.Abstractions.dll
Microsoft.Extensions.Logging.Abstractions.dll
Microsoft.Extensions.Logging.Configuration.dll
Microsoft.Extensions.Logging.dll
Microsoft.Extensions.Options.ConfigurationExtensions.dll
Microsoft.Extensions.Options.dll
Microsoft.Extensions.Primitives.dll
NinaOtel.Abstractions.dll
NinaOtel.Addons.NightSummary.dll
NinaOtel.Addons.OnStepX.dll
NinaOtel.Addons.PHD2.dll
NinaOtel.Addons.TargetScheduler.dll
NinaOtel.Core.dll
NinaOtel.Plugin.deps.json
NinaOtel.Plugin.dll
NinaOtel.Plugin.pdb
NinaOtel.Plugin.runtimeconfig.json
OpenTelemetry.Api.ProviderBuilderExtensions.dll
OpenTelemetry.Api.dll
OpenTelemetry.Exporter.OpenTelemetryProtocol.dll
OpenTelemetry.dll
System.Diagnostics.DiagnosticSource.dll
System.Security.Cryptography.ProtectedData.dll
ENTRIES
)"

if [[ "$archive_entries" != "$expected_entries" ]]; then
  printf 'Unexpected archive contents.\nExpected:\n%s\nActual:\n%s\n' "$expected_entries" "$archive_entries" >&2
  exit 1
fi

assert_missing_required_file_fails() {
  local missing_file="$1"
  local missing_output="$work_dir/missing-${missing_file//[^[:alnum:]]/-}-output"
  local missing_zip="$work_dir/missing-${missing_file//[^[:alnum:]]/-}.zip"
  mkdir -p "$missing_output"

  while IFS= read -r entry; do
    if [[ "$entry" == "$missing_file" ]]; then
      continue
    fi

    touch "$missing_output/$entry"
  done <<< "$expected_entries"

  if "$repo_root/scripts/package-plugin.sh" "$missing_output" "$missing_zip" >/dev/null 2>&1; then
    printf 'Expected packaging to fail when %s is missing.\n' "$missing_file" >&2
    exit 1
  fi
}

assert_missing_required_file_fails "OpenTelemetry.Exporter.OpenTelemetryProtocol.dll"
assert_missing_required_file_fails "System.Security.Cryptography.ProtectedData.dll"

missing_dependency_output="$work_dir/missing-dependency-output"
mkdir -p "$missing_dependency_output"
touch "$missing_dependency_output/NinaOtel.Plugin.dll"

if "$repo_root/scripts/package-plugin.sh" "$missing_dependency_output" "$work_dir/missing-dependency.zip" >/dev/null 2>&1; then
  printf 'Expected packaging to fail when NinaOtel.Core.dll and NinaOtel.Abstractions.dll are missing.\n' >&2
  exit 1
fi

#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  printf 'Usage: %s <plugin-build-output-dir> <zip-path>\n' "$(basename "$0")" >&2
  exit 2
fi

output_dir="$1"
zip_path="$2"

if [[ ! -d "$output_dir" ]]; then
  printf 'Plugin build output directory does not exist: %s\n' "$output_dir" >&2
  exit 1
fi

zip_dir="$(dirname "$zip_path")"
zip_name="$(basename "$zip_path")"
mkdir -p "$zip_dir"
zip_dir_abs="$(cd "$zip_dir" && pwd)"
zip_abs="$zip_dir_abs/$zip_name"

stage_dir="$(mktemp -d "${TMPDIR:-/tmp}/nina-otel-package.XXXXXX")"
trap 'rm -rf "$stage_dir"' EXIT

file_count=0
while IFS= read -r file; do
  cp "$file" "$stage_dir/"
  file_count=$((file_count + 1))
done < <(
  find "$output_dir" -maxdepth 1 -type f | while IFS= read -r file; do
    name="$(basename "$file")"
    case "$name" in
      NinaOtel.*|OpenTelemetry*.dll|Microsoft.Extensions*.dll|Microsoft.Bcl*.dll|System.Diagnostics.DiagnosticSource.dll|System.Security.Cryptography.ProtectedData.dll)
        printf '%s\n' "$file"
        ;;
    esac
  done | sort
)

if [[ $file_count -eq 0 ]]; then
  printf 'No NinaOtel package files found in: %s\n' "$output_dir" >&2
  exit 1
fi

required_files=(
  NinaOtel.Plugin.dll
  NinaOtel.Core.dll
  NinaOtel.Abstractions.dll
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
  OpenTelemetry.Api.ProviderBuilderExtensions.dll
  OpenTelemetry.Api.dll
  OpenTelemetry.Exporter.OpenTelemetryProtocol.dll
  OpenTelemetry.dll
  System.Diagnostics.DiagnosticSource.dll
)

for required_file in "${required_files[@]}"; do
  if [[ ! -f "$stage_dir/$required_file" ]]; then
    printf 'Required plugin package file was not found: %s\n' "$output_dir/$required_file" >&2
    exit 1
  fi
done

rm -f "$zip_abs"

if command -v zip >/dev/null 2>&1; then
  (cd "$stage_dir" && zip -qr "$zip_abs" .)
elif command -v pwsh >/dev/null 2>&1 || command -v powershell >/dev/null 2>&1; then
  ps_stage="$stage_dir"
  ps_zip="$zip_abs"
  if command -v cygpath >/dev/null 2>&1; then
    ps_stage="$(cygpath -w "$stage_dir")"
    ps_zip="$(cygpath -w "$zip_abs")"
  fi
  ps_bin="pwsh"
  if ! command -v pwsh >/dev/null 2>&1; then
    ps_bin="powershell"
  fi
  PACKAGE_STAGE="$ps_stage" PACKAGE_ZIP="$ps_zip" "$ps_bin" -NoProfile -Command \
    '$ErrorActionPreference = "Stop"; Compress-Archive -Path (Join-Path $env:PACKAGE_STAGE "*") -DestinationPath $env:PACKAGE_ZIP -Force'
else
  printf 'Neither zip nor PowerShell is available to create the archive.\n' >&2
  exit 1
fi

printf 'Created %s with %d file(s).\n' "$zip_abs" "$file_count"

#!/usr/bin/env bash
set -euo pipefail

if [[ -z "${DOTNET:-}" ]]; then
  if command -v dotnet >/dev/null 2>&1; then
    DOTNET=dotnet
  elif command -v mise >/dev/null 2>&1; then
    DOTNET="$(mise which dotnet)"
  else
    echo "dotnet was not found. Install dotnet or run via mise." >&2
    exit 127
  fi
fi

bash tests/package-plugin-tests.sh
"$DOTNET" restore NinaOtel.sln
"$DOTNET" build NinaOtel.sln --no-restore
"$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-build

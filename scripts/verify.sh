#!/usr/bin/env bash
set -euo pipefail

bash tests/package-plugin-tests.sh
dotnet restore NinaOtel.sln
dotnet build NinaOtel.sln --no-restore
dotnet test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-build

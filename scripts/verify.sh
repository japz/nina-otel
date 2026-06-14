#!/usr/bin/env bash
set -euo pipefail

dotnet restore NinaOtel.sln
dotnet build NinaOtel.sln --no-restore
dotnet test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --no-build

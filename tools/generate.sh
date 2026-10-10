#!/usr/bin/env bash
# Regenerates the OpenAPI-derived DTOs in src/Librus.Api/Generated.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet run --project tools/Librus.OpenApiGen -- openapi/api_v3_openapi.json src/Librus.Api/Generated Librus.Api.Models LibrusApi
dotnet run --no-build --project tools/Librus.OpenApiGen -- openapi/wiadomosci_librus_pl_api_openapi.json src/Librus.Api/Generated Librus.Api.Wiadomosci.Models Wiadomosci --no-operations

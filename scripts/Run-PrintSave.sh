#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"
exec dotnet run --project "$PROJECT_DIR/src/PrintSaveApp/PrintSaveApp.csproj" -- "$@"

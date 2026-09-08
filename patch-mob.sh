#!/usr/bin/env bash
# Patch one Type1 .mob integer in place (ability * percent or [warlike] style).
# Do not use a Windows PVF editor Save on the .mob.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
if [[ $# -lt 4 ]]; then
  echo "Usage: $0 <input.pvf> <output.pvf> <mob-path> <name>=<int> [<name>=<int> ...]"
  echo "Example: $0 Script.pvf Script.out.pvf monster/Tau/TauGuard.mob warlike=80"
  exit 1
fi

exec dotnet run --project "$ROOT/Tool/PvfType1Patch/PvfType1Patch.csproj" -c Release -- "$@"

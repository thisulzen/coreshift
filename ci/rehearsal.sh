#!/usr/bin/env bash
# What CI runs after the unit tests: build a small legacy bank, migrate it, and fail on any
# unexplained reconciliation break. Then prove the gate works: a migration with a deliberate
# rounding bug must be refused.
set -euo pipefail
cd "$(dirname "$0")/.."
coreshift() { dotnet run --project CoreShift.Migrator -c Release -- "$@"; }

db/load-legacy.sh 2000 42
coreshift migrate
coreshift reconcile

coreshift migrate --fault rounding
if coreshift reconcile > /dev/null; then
    echo "A migration with a rounding bug passed reconciliation. The gate is broken."
    exit 1
fi
echo "Clean migration passed. The deliberately broken one was refused."

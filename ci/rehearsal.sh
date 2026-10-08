#!/usr/bin/env bash
# What CI runs after the unit tests: build a small legacy bank and rehearse the whole cutover
# (freeze, extract, transform, load, reconcile, go/no-go, rollback), failing on a no-go. Then prove
# the gate works: a rehearsal with a deliberate rounding bug must come out no-go.
set -euo pipefail
cd "$(dirname "$0")/.."
coreshift() { dotnet run --project CoreShift.Migrator -c Release -- "$@"; }

db/load-legacy.sh 2000 42
coreshift rehearse

if coreshift rehearse --fault rounding > /dev/null; then
    echo "A rehearsal with a rounding bug came out go. The gate is broken."
    exit 1
fi
echo "Clean rehearsal was go. The deliberately broken one was no-go, as it should be."

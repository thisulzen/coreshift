#!/usr/bin/env bash
# What CI runs after the unit tests, against a small legacy bank:
#   1. a full rehearsal (freeze, extract, transform, load, reconcile, go/no-go, rollback) must be go
#   2. a rehearsal with each deliberate mapping bug must be no-go, so the gate is proven, not assumed
#   3. a run killed inside a cohort must resume and finish go
#   4. the scam watch must list exactly the planted scams
set -euo pipefail
cd "$(dirname "$0")/.."
coreshift() { dotnet run --project CoreShift.Migrator -c Release -- "$@"; }

db/load-legacy.sh 2000 42
coreshift rehearse

for fault in rounding drop-pending date-format accrual joint-holders flags; do
    if coreshift rehearse --fault "$fault" > /dev/null; then
        echo "A rehearsal with the '$fault' bug came out go. The gate is broken."
        exit 1
    fi
    echo "  --fault $fault: no-go, as it should be"
done

code=0
coreshift rehearse --crash-in-cohort 2 > /dev/null || code=$?
if [ "$code" != 70 ]; then
    echo "Expected the simulated crash (exit 70), got exit $code."
    exit 1
fi
out=$(coreshift resume)
grep -q "Resuming run" <<< "$out" || { echo "Nothing was left to resume after the crash."; exit 1; }

# The scam watch must list exactly the accounts the generator emptied with a scam, no more and no fewer
diff <(coreshift scam-watch | awk 'NR > 1 { print $1 }' | sort) <(sort CoreShift.Generator/output/legacy/planted_scams.txt) \
    || { echo "The scam watch doesn't match the planted scams (< listed, > planted)."; exit 1; }

echo "Clean rehearsal was go, every deliberate bug was no-go, a run killed mid-load resumed to go, and the scam watch was exact."

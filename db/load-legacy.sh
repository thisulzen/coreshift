#!/usr/bin/env bash
# Loads the legacy bank into SQL Server in Docker: starts the database, recreates the legacy
# schema, bulk-loads the CSV extract and checks nothing was lost or changed on the way in.
# Usage: db/load-legacy.sh                     load the existing extract (makes one if there isn't one)
#        db/load-legacy.sh customers [seed]    generate a fresh extract first (seed defaults to 42)
set -euo pipefail
cd "$(dirname "$0")/.."
extract=CoreShift.Generator/output/legacy

# Passwords live in .env, which git ignores: SA for this script, CORESHIFT for the tool's own login.
# Each is made at random the first time it's missing.
[ -f .env ] || echo "MSSQL_SA_PASSWORD=Cs1$(openssl rand -hex 12)" > .env
grep -q '^CORESHIFT_PASSWORD=' .env || echo "CORESHIFT_PASSWORD=Cs1$(openssl rand -hex 12)" >> .env
CORESHIFT_PASSWORD=$(sed -n 's/^CORESHIFT_PASSWORD=//p' .env)

if [ $# -gt 0 ] || [ ! -f "$extract/control_totals.csv" ]; then
    echo "==> Generating legacy bank data"
    (cd CoreShift.Generator && dotnet run -- "${1:-5000}" "${2:-42}")
fi

echo "==> Starting SQL Server"
docker compose up -d --wait

# Runs sqlcmd inside the container, which already has the password in its environment
sql() {
    docker compose exec -T sql bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b "$@"' sqlcmd "$@"
}

echo "==> Creating legacy schema"
sql -i /coreshift/db/01-legacy-schema.sql

# BULK INSERT maps CSV fields to columns by position, so a column added to Models.cs but not
# to the schema would silently shift data into the wrong columns. Refuse to load if they differ.
echo "==> Checking CSV columns match the tables"
for table in customers accounts account_holders transactions; do
    csv=$(head -1 "$extract/$table.csv")
    db=$(sql -h -1 -y 8000 -Q "SET NOCOUNT ON; SELECT STRING_AGG(COLUMN_NAME, ',') WITHIN GROUP (ORDER BY ORDINAL_POSITION)
        FROM CoreShift.INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'legacy' AND TABLE_NAME = '${table}_extract'" | tr -d '[:space:]')
    if [ "$csv" != "$db" ]; then
        printf 'Column mismatch in %s\n  csv:   %s\n  table: %s\n' "$table" "$csv" "$db"
        exit 1
    fi
done

echo "==> Loading CSV extract"
sql -i /coreshift/db/02-load-legacy.sql

# The database must reproduce the generator's row counts and money totals to the cent
echo "==> Checking control totals"
expected=$(tail -n +2 "$extract/control_totals.csv")
actual=$(sql -h -1 -W -Q "SET NOCOUNT ON; SELECT line FROM (VALUES
    (1, (SELECT CONCAT('customers,', COUNT(*), ',', ISNULL(SUM(ANNUAL_INCOME), 0)) FROM CoreShift.legacy.customers)),
    (2, (SELECT CONCAT('accounts,', COUNT(*), ',', ISNULL(SUM(BALANCE), 0)) FROM CoreShift.legacy.accounts)),
    (3, (SELECT CONCAT('account_holders,', COUNT(*), ',') FROM CoreShift.legacy.account_holders)),
    (4, (SELECT CONCAT('transactions,', COUNT(*), ',', ISNULL(SUM(AMOUNT), 0)) FROM CoreShift.legacy.transactions))
    ) v(n, line) ORDER BY n")
if [ "$expected" != "$actual" ]; then
    printf 'Control totals differ\n  expected:\n%s\n  loaded:\n%s\n' "$expected" "$actual"
    exit 1
fi
echo "$actual" | sed 's/^/    /'

echo "==> Running integrity checks"
sql -i /coreshift/db/04-legacy-checks.sql

echo "==> Setting the freeze and the coreshift login (read-only on legacy)"
sql -v pw="$CORESHIFT_PASSWORD" -i /coreshift/db/03-access.sql

# Changelog

Every change since the stage 7 commit, grouped by kind, with what proved it works.

## 2026-10-24

### Fixes

- **Run numbers jumped from #6 to #1006.** SQL Server skips 1,000 identity values after an unclean restart. Turned the identity cache off. `db/05-target-schema.sql`. *Proved:* killed SQL Server with `docker kill`; the next id was 7.
- **A rehearsal after go-live wiped the live bank.** Every rehearsal and `migrate` starts by emptying the target, including after a cutover has committed. Both now refuse while legacy is retired by a committed cutover; loading a new legacy bank lifts it. `CoreShift.Migrator/Cutover.cs` (`EnsureTargetNotLive`), `Program.cs`, `db/02-load-legacy.sql`. *Proved:* after a real cutover, `rehearse`, `cutover` and `migrate` were refused and `reconcile` still ran.
- **Two runs started at the same moment could both get in.** The start is now one locked `INSERT … WHERE NOT EXISTS`. `Cutover.cs`. *Proved:* of three rehearsals started at once, one ran and one was refused with "Another run started at the same moment" (the third hit the next bug).
- **Two commands starting at once could fail with "entity was modified during DDL execution".** Each start re-runs the schema script, which recreates the freeze triggers. The script now takes an application lock, so starts queue for it. `db/05-target-schema.sql`. *Proved:* four rehearsals started at the same instant; one ran and passed, three were refused with "Run 10 hasn't finished".
- **The freeze fingerprint could miss a change.** It was `CHECKSUM_AGG(BINARY_CHECKSUM(*))`, a 32-bit checksum. Now each legacy table's row count and highest `rowversion`: exact, and 0.1 s instead of 1.9 s. SHA-256 over every row was also exact but took 7.7 s. `db/02-load-legacy.sql`, `db/05-target-schema.sql`. *Proved:* a rolled-back write leaves it unchanged; a committed write of the same value changes it.
- **The open-date check could crash the reconciliation.** It built a swapped date with `DATEFROMPARTS` behind a `DAY() <= 12` guard, but SQL Server doesn't promise to evaluate `AND` left to right. Adding checks changed the plan and it built month 25. Now compares year, month and day directly. `db/06-reconcile.sql`. *Proved:* the run that crashed resumed and passed; the date-format bug is still named.

### First run on a new machine

- **The tool couldn't create its tables on a new database.** Creating the target's foreign keys needs `REFERENCES` permission, which the `coreshift` login didn't have. Every pipeline on GitHub or Azure DevOps, which always start empty, would have failed; local runs passed only because the tables already existed. Granted on the target schema. `db/03-access.sql`. *Proved:* the full CI script passes in a clean copy of the repo with no `.env`, against a brand-new SQL Server container and volume, in 2 min 47 s. The project has to live under your home folder for Colima to share it with the container.

### Scale (found by re-timing 100,000 customers)

- **Every cohort got slower than the last, so time grew with the square of the bank.** Cohort 1 took 4.2 s and cohort 130 took 8.7 s. `AddWithValue` sends .NET strings as `NVARCHAR`; every column here is `VARCHAR` or `CHAR`, so SQL Server converted the column instead and couldn't seek a single index (the server collation is `SQL_Latin1_General_CP1_CI_AS`): each cohort scanned all 11.6 million legacy transactions plus the whole growing target. Text parameters are now sent as `VARCHAR`, fixed once in `Db.Cmd`, which every query goes through. *Proved:* the same 100k run, killed with `kill -9` at cohort ~135 and resumed on the fix, dropped to a flat 1.6 s a cohort to the end (1.61 s for the first ten of a fresh run, 1.58 s for the last ten).
- **Rollback took 4 min 25 s at 100k and wrote gigabytes of log**, deleting 11.6 million transactions row by row. Transactions and holders, which nothing references, are now truncated; it's still inside the same transaction. Accounts and customers, which foreign keys point at, are still deleted. `Migration.cs`. *Proved:* 2.1 s.
- **Adding the rowversion after the load rewrote every row: 43 s and 4 GB of log at 100k.** The legacy tables now have the column from the start, and each loads through a view of its other columns, generated from the table so it can't drift, so rows are stamped as they're bulk-loaded. `db/01-legacy-schema.sql`, `db/02-load-legacy.sql`, `db/load-legacy.sh` (the column check now compares the CSV with the view). *Proved:* the log grew from 72 MB to 136 MB for the whole 100k load, and the load and checks took 3 min 46 s against 5 min 52 s.
- **Measured at 100,000 customers** (223,303 accounts, 11.6 million transactions, 2,100 duplicates, 565 planted scams): generate 50 s; load and check 3 min 46 s; a full rehearsal 8 min 1 s, go, 0 unexplained breaks, log 584 MB; scam watch exact, 565 of 565, in 18 s. Before the fixes, one 100k load and a rehearsal heading for about 30 minutes grew the log to 15.9 GB.

### Reconciliation

- **New checks:** account status (through its own lookup, not the mapping file's), account terms (interest rate, overdraft and credit limits, available balance, repayment, hardship), who holds each account and in what role, and compliance flags (PEP, vulnerable, deceased, bankrupt, AML risk rating can't drop), merges included. `db/06-reconcile.sql`. *Proved:* a clean rehearsal still has 0 unexplained breaks.
- **Two new deliberate bugs** to prove them: `--fault joint-holders` (2,337 joint holders lost) and `--fault flags` (113 customers lose a PEP or vulnerability flag). Both no-go. `Migration.cs`.
- **The usage text lists faults from the code**, so it can't fall behind. `Program.cs`.
- **Removed write-only control totals** (`legacy_accounts`, `legacy_balance` on `ops.cutover_runs`). Nothing read them; the fingerprint covers it. `Cutover.cs`, `db/05-target-schema.sql`.

### Scam watch

- **A list for the fraud team at cutover.** A cutover is when scammers call: "your bank is moving, keep your money safe in this account". `ops.scam_watch` lists accounts that, in the week up to the extract, sent at least $2,000 to people they had never paid and sent more than they kept. It reads legacy only and never affects go/no-go. Shown by `coreshift scam-watch`, in a new dashboard section, and as a count in every run's audit log. `db/05-target-schema.sql`, `Program.cs`, `Cutover.cs`, `Dashboard.cs`, `dashboard.html`.
- **The generator plants scams** to test it against: about 1 in 100 households, on the extract day, move their savings across and send most of it to a new payee. They use their own randomness, seeded per household, so the rest of the bank is unchanged. The planted accounts are written to `planted_scams.txt`, the answer key. `LegacyBankGenerator.cs`, `ExtractSummary.cs`, `CoreShift.Generator/Program.cs`.
- **CI checks the list is exact.** `ci/rehearsal.sh`. *Proved:* on four banks of 5,000 customers (seeds 7, 99, 2024, 42), 99 planted scams, all 99 listed and nothing else.

### Dashboard

- **Restyled** in the style of jimo.ai: black background with a glow, DM Sans, numbered sections, glassy cards. CSS only. `CoreShift.Migrator/dashboard.html`.
- **Honest scores.** "Pass rate 33%" counted deliberate bugs as failures. Now "deliberate bugs caught: 6 of 6" and "clean runs passed".
- **Live.** Refreshes every 3 s and keeps the rehearsal you clicked, so a run started in the terminal shows up cohort by cohort.
- **The first run on a fresh bank was invisible until it finished.** The page stopped early when no rehearsal had finished yet, before drawing the run log. The log now draws on its own. *Proved:* watched run #2 go from "loaded" to its result without a reload, all 12 cohorts listed.
- **The run title read "no_go, no-go".** Now "rolled back, no-go", "committed, go".
- **The real cutover was labelled "clean"** on the chart, like a rehearsal. Now "cutover".
- **Screenshot retaken**, from a fresh run of the whole story with this version: six bugs caught, a clean rehearsal, a real cutover, and the scam watch's 28 accounts. Planted scams changed one number in the story: the rounding bug now hits 7,289 balances, $7.82 out. `docs/dashboard.png`.
- **The scam watch sits above the run log**, so it's on screen without scrolling past 30 lines of cohorts.

### Tooling

- **Clear error when SQL Server can't be reached**, with the command to start it and the real reason (a stopped container and a wrong password read differently). `Db.cs`.
- **CI proves more:** every deliberate bug must be no-go, and a run killed inside a cohort must resume to go. `ci/rehearsal.sh`. *Proved:* the whole script passes locally in about 3 minutes.
- **Deleted `db/03-legacy-queries.sql`**, practice queries nothing ran or linked to.
- **`migrate` refuses while a rehearsal or cutover is unfinished**, since it would empty the target that run is loading. `Program.cs`. *Proved:* "Run 11 owns the target until it finishes".
- **A missing or malformed mapping file crashed with a stack trace (exit 134).** The mapping was loaded outside the error handling. Now it's one line, "Failed: Could not find file …", and exit 1. `Program.cs`.

### Docs

- **README** covers the new checks, the six faults, the rowversion freeze, the go-live guard, the new test results and what this round of testing turned up. Links here. `README.md`.

### Security

- **The tool no longer logs in as `sa`.** It has its own `coreshift` login that can read legacy but not change it, and owns only the target and the migration's bookkeeping. So no bug in the tool can damage the old bank, whatever the freeze is doing. Everything that touches legacy tables or database settings moved out of the tool's startup into an administrator script that `load-legacy.sh` runs as `sa`: the freeze triggers and switch, the schemas, the identity-cache setting, and the login itself, with its password generated into `.env`. As a bonus, the tool's startup no longer recreates triggers, the root cause of the DDL clash below. `db/03-access.sql` (new), `db/01-legacy-schema.sql`, `db/02-load-legacy.sql`, `db/05-target-schema.sql`, `db/load-legacy.sh`, `Db.cs`. *Proved:* as `coreshift`, update, delete, truncate, drop, disabling the freeze trigger, altering a legacy table, changing a database setting and creating a login were all refused; reading legacy and a full rehearsal worked.

- **Removed a known high-severity vulnerability.** Microsoft.Data.SqlClient 7.1.1 (the newest) pulls in System.Security.Cryptography.Pkcs 9.0.18 (GHSA-846f-f764-76mx). Pinned Pkcs 10.0.13. `CoreShift.Migrator/CoreShift.Migrator.csproj`. *Proved:* `dotnet list package --vulnerable --include-transitive` reports none.
- **SQL Server is pinned to 2022 CU27**, the build every rehearsal here ran on. `2022-latest` had already moved to a different image, so CI would have tested against a server nobody had run. `docker-compose.yml`.
- **The GitHub Actions token is read-only.** The workflow only reads the code. `.github/workflows/ci.yml`.
- **CI warnings cleared.** GitHub flagged `actions/checkout@v4` and `actions/setup-dotnet@v4` for running on the deprecated Node.js 20, and `ubuntu-latest` for moving to Ubuntu 26. Updated to `checkout@v7` and `setup-dotnet@v6` (Node.js 24, nothing in their release notes affects this workflow), and pinned the runner to `ubuntu-24.04`, the image CI last passed on, in both pipelines. `.github/workflows/ci.yml`, `azure-pipelines.yml`.
- **The build now fails on any high or critical package advisory**, direct or transitive, instead of printing a warning nobody reads. `Directory.Build.props`. *Proved:* with the pin removed the build stops with `error NU1903`.

### Tests

Tried against the real database, each one passed unless a fix above says otherwise:

- **Tampering with the target** the way a subtle mapping bug would: a closed account reopened, a credit limit and a hardship flag changed, a joint holder made primary, an account moved to the wrong customer. All five caught and named.
- **Getting around the freeze**: disabling the trigger and writing a customer's address back unchanged. No-go: "Legacy changed during the freeze".
- **Go-live**: after a real cutover commits, `rehearse`, `cutover` and `migrate` are refused; `reconcile` still checks the live bank.
- **SQL Server stopped, or a wrong password**: "Can't connect to SQL Server on localhost:1433 (if it isn't running: `docker compose up -d`)" followed by the real reason, exit 1.
- **Bad input**: no command, an unknown command or an unknown fault give the usage and exit 2; a code missing from the mapping stops the load with "No accountStatus mapping for 'OPEN'".
- **The smallest bank**: one household, one cohort, go.
- **The duplicate matcher is checked on banks it wasn't tuned on.** A wrong merge is filed as an explained break, so the reconciliation can't catch one; the new test checks the matcher merges exactly the planted duplicates for five more seeds. The generator now records which records it duplicated. `CoreShift.Tests/Tests.cs`, `LegacyBankGenerator.cs`. 26 → 31 tests. *Also ran once:* 100 banks of 2,000 customers, 4,362 planted duplicates, no wrong or missed merges.

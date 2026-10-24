# CoreShift

Move a bank's accounts from an old core banking system to a new one, and prove that not a cent went missing on the way.

When two banks merge, one of them eventually moves every account onto the other's core system. Copying the data is the easy part. The hard part is proving afterwards that every account and every transaction arrived intact, and having a way back if they didn't. CoreShift is a working model of that job, built on synthetic data.

![Rehearsal dashboard](docs/dashboard.png)

The dashboard above tells the story of one afternoon's rehearsals: a mapping with six bugs in it, each one caught and named by the reconciliation, fixed one at a time until the breaks hit zero, then a real cutover that committed. It updates live, so a run started in the terminal shows up cohort by cohort.

## Run it

You need the .NET 10 SDK and Docker (Docker Desktop or Colima). With Colima, keep the project under your home folder: that's all Colima shares with containers.

```bash
db/load-legacy.sh 5000                                    # build a 5,000-customer legacy bank in SQL Server
dotnet run --project CoreShift.Migrator -- rehearse       # a full cutover as a dry run
dotnet run --project CoreShift.Migrator -- dashboard      # then open http://localhost:5080
```

The first run writes random passwords to `.env`, which git ignores: one for `sa`, which only `load-legacy.sh` uses, and one for the tool's own `coreshift` login. The other commands are `migrate`, `reconcile`, `cutover`, `resume` and `scam-watch`; run the tool with no arguments to see them all. After a real `cutover` commits, the target is the live bank and nothing will empty it again: load a new legacy bank with `db/load-legacy.sh` to rehearse again.

Every change since the original seven stages is in [CHANGELOG.md](CHANGELOG.md), each with what proved it works.

## How it works

The project follows a cutover from end to end, in seven stages.

| Stage | What it does |
|---|---|
| 1. Legacy bank | Generates a synthetic bank one household at a time, with realistic products and deliberately messy legacy data |
| 2. Legacy load | Bulk-loads the bank into SQL Server and refuses to finish unless control totals match to the cent |
| 3. Mapping | Moves legacy into a clean target schema using versioned rules in a config file, in cohorts, and merges duplicate customers |
| 4. Reconciliation | Compares the two sides in independent SQL and files every difference as a break with a reason |
| 5. Tests and CI | 31 unit tests, plus a pipeline that runs a full rehearsal, proves every deliberate bug comes out no-go, kills a run mid-load to prove it resumes, and checks the scam watch exactly. The build fails on any high or critical package vulnerability |
| 6. Cutover | A state machine that freezes legacy, migrates, reconciles, decides go or no-go, then commits or rolls back, and survives being killed half-way |
| 7. Dashboard | Rehearsal history, breaks by reason, the latest run's audit log live while it's going, and the scam watch |

### The legacy bank

Customers come with a job and an income that fits it, KYC and AML status, tax residency, and a credit bureau check if they've applied for credit. Accounts cover everyday, offset, savings, term deposits, home loans, personal loans and credit cards, including joint accounts, closed accounts, dormant accounts and deceased estates. About three and a half months of day-to-day banking sits behind every balance, so each balance is exactly the sum of its own history.

It's also messy on purpose, because real legacy data is. Some customers were keyed in twice, with a typo or with the day and month of their birthday swapped. Dates come in two formats depending on whether the record predates a 2010 system upgrade. Names have stray spaces, BSBs are missing their dash, phone numbers come in three spellings, and long account names were cut off at 30 characters.

### Mapping

`mapping/mapping.v1.json` says which legacy column feeds which target column, and through which rule: trim it, fix its capitals, read either date format, add the BSB dash, turn three phone spellings into one, or look a code up in the new product catalogue. A code the file doesn't know stops the migration instead of slipping through blank. Every run records the mapping version.

Duplicate customers are matched on postcode and suburb, then a birthday (or the same with day and month swapped), a surname within two edits, and a first name that agrees or is the right initial. The 5,000-customer bank has 120 planted duplicates; the matcher finds exactly 120. That needs checking more widely than one bank, because a wrong merge is filed as an explained break and the reconciliation can't catch it: a test checks five more banks, and a one-off run over 100 banks of 2,000 customers (4,362 planted duplicates) found no wrong or missed merge. Accounts move in cohorts of 1,000, and each cohort is deleted and reinserted inside one transaction, so running the migration twice gives exactly the same result as running it once.

### Reconciliation

`db/06-reconcile.sql` never calls the mapping code. It reads both sides with its own SQL, so a bug in the mapping can't hide itself. It checks:

- record counts and the bank-wide balance
- every account's balance, accrued interest and open date
- every account's status, through its own lookup rather than the mapping file's, so a closed account can't come back open
- the terms each customer is held to: interest rate, overdraft and credit limits, available balance, repayment and hardship
- who holds each account, and in what role
- compliance flags: if any legacy record of a person says PEP, vulnerable, deceased or bankrupt, so must the target customer, and their AML risk rating can't drop, merges included
- a SHA-256 of each account's ordered transaction history

Merged duplicates are filed as explained breaks with their evidence; anything else is unexplained and means no-go.

Each of the six deliberate mapping bugs (`--fault`) is caught and named:

| Bug injected | What the reconciliation reported |
|---|---|
| Balances rounded to dollars | 7,289 balances rounded to whole dollars, bank total $7.82 out |
| Pending card transactions dropped | 3,263 accounts with pending transactions not migrated |
| Dates read month-first | 3,945 open dates read as MM/dd, 7,844 histories altered |
| Accrued interest not carried | 3,608 accounts with interest accrual timing breaks |
| Joint holders dropped | 2,337 joint holders not migrated |
| PEP and vulnerability flags not carried | 113 customers with a compliance flag lost |

Hand-tampering with a loaded target is caught the same way: a closed account reopened, a credit limit or hardship flag changed, a joint holder made primary, and an account moved to the wrong customer were each found and named.

### Cutover

`STARTED → FROZEN → EXTRACTED → TRANSFORMED → LOADED → RECONCILED → GO or NO_GO → COMMITTED or ROLLED_BACK`

While a run is going, triggers on the legacy tables refuse every write. A fingerprint taken at extract, each legacy table's row count and highest `rowversion`, shows whether anything changed anyway: SQL Server stamps every committed insert or update with a new, ever-higher rowversion, so it catches even a write that puts back the same value. The state is saved after every step and each cohort commits together with its "loaded" mark, so a run that dies anywhere picks up where it stopped with `resume`. A no-go empties the target and unfreezes legacy in one transaction. `rehearse` runs the same thing as a dry run that always rolls back.

Once a cutover commits, the target is the live bank. Every rehearsal and `migrate` starts by emptying the target, so all of them refuse to run until a new legacy bank is loaded. Only one run can be unfinished at a time, even when several start at the same instant.

These were tested against a real database:

- a crash inside a cohort, and a real `kill -9` mid-load (7 of 44 cohorts committed, no half-written rows, `resume` finished the rest)
- a write to frozen legacy (refused), and an insider disabling the trigger to write a customer's address back unchanged (no-go: "Legacy changed during the freeze")
- four runs started at the same instant: one ran, three were refused
- every command after go-live: `rehearse`, `cutover` and `migrate` refused, `reconcile` still checks the live bank
- a no-go rollback that left legacy byte-for-byte unchanged

### Who can change what

The tool logs in as its own `coreshift` login, which can read the legacy bank but not change it. SQL Server refuses it every way of doing so: update, delete, truncate, drop, disabling the freeze, altering a legacy table, changing a database setting, creating a login. It owns only the target and the migration's bookkeeping, so no bug in the tool can damage the old bank, whatever the freeze is doing. The freeze triggers, the schemas and the login itself are set up by an administrator script, `db/03-access.sql`, which `load-legacy.sh` runs as `sa`.

### Scam watch

A cutover is when scammers call: "your bank is moving, keep your money safe in this account". `coreshift scam-watch` gives the fraud team the accounts that, in the week up to the extract, sent at least $2,000 to people they had never paid and sent more than they kept. The dashboard shows the same list, and every run's audit log records the count. It reads legacy only and never affects go/no-go.

The generator plants such scams in about 1 in 100 households on the extract day, with their own randomness so the rest of the bank doesn't change, and writes the planted accounts to an answer key. CI checks the scam watch against it exactly. On four banks of 5,000 customers it found all 99 planted scams and nothing else, and at 100,000 customers all 565, in 18 seconds.

## What testing turned up

Stress and failure testing found real bugs, and honestly they taught me more than building the thing did.

- The database total came out 11 cents short of the generator's. Fortnightly home loans carried fractions of a cent, and the CSV rounded them away. The generator's own check missed it because both sides of its comparison carried the same fractions. The control totals check came out of this one, and so did the rule that the reconciliation never shares code with the mapping.
- Holding the whole bank in memory took 1.1 GB at 20,000 customers. Writing each household out as soon as it's done keeps it at about 60 MB, even at 100,000.
- Under SQL Server's default FULL recovery model, one big load grew the transaction log to 2.1 GB. This database gets rebuilt every time, so it now runs in SIMPLE recovery: 72 MB, and faster.
- Deleting and regenerating the output folder left the container looking at a folder that no longer existed. The container now mounts the project root instead.
- After a restart, the container reported healthy before the database had finished opening. The health check now reads from the database itself.
- The reconciliation first labelled 33 rounded balances as plain differences. They were exact 50-cent amounts, where C#'s round-half-to-even and SQL's round-half-up disagree.
- Running a rehearsal after the real cutover emptied the live bank, because every rehearsal starts by clearing the target. That's the worst bug a migration tool can have, and nothing had tested for it. Nothing can touch the target after go-live now.
- The freeze fingerprint was a 32-bit `CHECKSUM`, which can miss a change. SHA-256 over every row was exact but took 7.7 s, twice per cutover. Row counts and the highest `rowversion` are exact and take 0.1 s.
- Adding reconciliation checks made it crash with "Cannot construct data type date". The open-date check built a swapped date behind a `DAY() <= 12` guard, but SQL Server doesn't promise to evaluate `AND` left to right, and the new query plan built month 25. It compares year, month and day directly now.
- Starting several commands at once failed with "entity was modified during DDL execution": each start recreates the freeze triggers. The schema script now takes an application lock.
- After an unclean restart, run numbers jumped from #6 to #1006: SQL Server's identity cache. It's off now.
- The database driver pulled in a package with a high-severity advisory, and the build only warned. The package is pinned to a fixed version, and the build now fails on any high or critical advisory.
- Re-timing 100,000 customers found the worst one: every cohort was slower than the last (4.2 s, then 8.7 s by cohort 130), so the time grew with the square of the bank. .NET sends strings as `NVARCHAR`, the columns are `VARCHAR`, and SQL Server converted the column instead and couldn't use a single index: every cohort scanned all 11.6 million legacy transactions and the whole growing target. Sending them as `VARCHAR`, in the one function every query goes through, made cohorts a flat 1.6 s and the full rehearsal 8 minutes instead of heading for 30.
- The same run's rollback took 4 minutes 25 seconds, deleting 11.6 million rows one by one, and stamping rowversions after the load rewrote every row. Between them, one 100k run grew the transaction log to 15.9 GB. Truncating the two tables nothing references (still inside the transaction) and stamping rows as they're bulk-loaded brought it to 2 seconds and 584 MB.

## Scale

| Customers | Transactions | Generate | Load and check | Full rehearsal | Log after both |
|---|---|---|---|---|---|
| 5,000 | 0.6 million | 4 s | under 30 s | about 37 s | not measured |
| 100,000 | 11.6 million | 50 s | 3 min 46 s | 8 min 1 s | 584 MB |

The generator stays at about 61 MB of memory at either size. At 100,000 customers a rehearsal spends 6 minutes loading 224 cohorts at a flat 1.6 seconds each, 78 seconds reconciling 11.6 million transactions, and 2 seconds rolling back; the scam watch takes 18 seconds. At 750,000 customers I'd generate households in parallel (they don't depend on each other), split the extract into several files and load them side by side, and run cohorts on several connections at once, since each one is already its own transaction. I haven't tried any of that yet.

## Layout

```
CoreShift.Generator/   the synthetic legacy bank (C#, Bogus)
CoreShift.Migrator/    the coreshift tool: migrate, reconcile, rehearse, cutover, resume, dashboard
CoreShift.Tests/       xUnit tests
mapping/               versioned mapping rules
db/                    legacy and target schemas, load, access, checks, reconciliation, scam watch
ci/                    the rehearsal CI runs
azure-pipelines.yml    Azure DevOps pipeline; .github/workflows/ci.yml runs the same steps on GitHub
CHANGELOG.md           every change since the seven stages, with what proved it
```

Kubernetes was optional in the plan and isn't here. The whole thing runs in one SQL Server container and one console app.

## Synthetic data only

Everything here is made up. There are no real banks, people, tax file numbers, licence numbers or card numbers. Emails use reserved example domains, and the BSB prefix and business names are invented.

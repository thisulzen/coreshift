# CoreShift

Move a bank's accounts from an old core banking system to a new one, and prove that not a cent went missing on the way.

When two banks merge, one of them eventually moves every account onto the other's core system. Copying the data is the easy part. The hard part is proving afterwards that every account and every transaction arrived intact, and having a way back if they didn't. CoreShift is a working model of that job, built on synthetic data.

## Where it's at

| Stage | What | Status |
|---|---|---|
| 1 | A realistic synthetic legacy bank | Done |
| 2 | The legacy bank loaded into SQL Server, with load checks | Done |
| 3 | A clean target schema and config-driven mapping, migrated in cohorts | Next |
| 4 | Reconciliation: counts, totals, per-account balances, transaction-history hashes | |
| 5 | Unit tests and a CI pipeline that fails on a broken reconciliation | |
| 6 | Cutover state machine with go/no-go and rollback | |
| 7 | Rehearsal dashboard and demo | |

## Run it

You need the .NET 10 SDK and Docker (Docker Desktop or Colima).

```bash
db/load-legacy.sh 5000      # generate a 5,000-customer bank and load it into SQL Server
db/load-legacy.sh           # reload the extract you already have
```

The first run writes a random SQL Server password to `.env`, which git ignores. A 5,000-customer bank takes about 15 seconds end to end. To poke around, open `db/03-legacy-queries.sql` and run the queries one at a time.

## The legacy bank

The generator (`CoreShift.Generator`) builds a bank one household at a time and writes it out the way an old core's nightly extract would: four CSV files plus a control totals file.

- Customers have a name, date of birth, address and contact details, a job and an income that fits it, KYC and AML status, tax residency, and a credit bureau check if they've ever applied for credit.
- Accounts cover everyday, offset, savings, term deposits, home loans, personal loans and credit cards. Joint accounts, closed accounts, dormant accounts and deceased estates are all in there.
- Transactions cover about three and a half months of day-to-day banking: pay, rent, bills, card spending, loan repayments, interest, fees. Card purchases from the last two days are still pending.

Balances aren't made up. Each account starts the history with a brought-forward balance, and every transaction moves it, so every balance is exactly the sum of its history. Home loans are capped by income, missed repayments show up in arrears and in the 24-month repayment history, and direct debits that would overdraw an account too far get declined.

The data is also messy on purpose, because real legacy data is:

- the same person keyed in twice, with a typo or with day and month swapped
- dates in two formats, depending on whether the record predates a 2010 system upgrade
- names in lowercase or padded with spaces, BSBs without the dash, phone numbers in three formats
- account names cut off at 30 characters
- interest accrued since the 1st that hasn't been paid yet

Stage 3 has to clean all of that up.

Run the same seed and you get the same bank, byte for byte. That matters later, when migration rehearsals get compared against each other.

## How the load proves nothing was lost

`db/load-legacy.sh` refuses to finish unless all of these pass:

1. The generator checks itself: every balance equals the sum of its posted transactions, and no amount carries a fraction of a cent.
2. The CSV headers must match the table columns, in order. `BULK INSERT` matches by position, so a mismatch would put data in the wrong column without complaining.
3. Foreign keys are enforced during the load. `BULK INSERT` skips them unless you ask, so the load asks, and a later check confirms SQL Server trusts the keys.
4. Row counts and money totals in the database must match the generator's control totals to the cent.
5. Every account's balance is checked against its history again inside the database, and no transaction can be dated before its account opened.

To check the checks, I corrupted the extract seven different ways. Every one was refused:

| Corruption | Caught by |
|---|---|
| A column renamed | Column check |
| A transaction for an account that doesn't exist | Foreign key |
| A duplicated transaction | Primary key |
| One amount changed by 1 cent | Control totals |
| The file cut short | Control totals |
| A balance edited, with the control totals fudged to hide it | Per-account reconciliation |
| Text in a money column | Type conversion |

## What testing turned up

Stress and failure testing found five real bugs. They're all fixed, and honestly they taught me more than building the thing did.

- The database total came out 11 cents short of the generator's. Fortnightly home loans carried fractions of a cent, and the CSV rounded them away. The generator's own check missed it, because both sides of its comparison carried the same fractions. The control totals check came out of this one.
- Holding the whole bank in memory took 1.1 GB at 20,000 customers. Writing each household out as soon as it's done keeps it at about 60 MB, even at 100,000.
- Under SQL Server's default FULL recovery model, one big load grew the transaction log to 2.1 GB. This database gets rebuilt from the extract every time, so it now runs in SIMPLE recovery: 72 MB, and faster.
- Deleting and regenerating the output folder left the container looking at a folder that no longer existed. The container now mounts the project root instead.
- After a restart, the container reported healthy before the database had finished opening, so a load could start too soon and fail. The health check now reads from the database itself.

## Scale

| Customers | Transactions | Generate | Peak memory | Load into SQL Server |
|---|---|---|---|---|
| 5,000 | 0.6 million | 4 s | 61 MB | 10 s |
| 100,000 | 11.6 million | 39 s | 61 MB | about 4 min |

At 750,000 customers I'd generate households in parallel (they don't depend on each other), split the extract into several files and load them side by side. The migration itself is planned to move in cohorts anyway (Stage 3).

One loose end: after the 100,000-customer load the data file stays at 2 GB, even when a smaller bank is loaded over it. SQL Server reuses the space but never hands it back on its own. I've left it, since shrinking database files has its own downsides, but it's the first thing you'd notice on a small disk.

## Layout

```
CoreShift.Generator/    the synthetic legacy bank (C#, Bogus)
db/                     SQL Server schema, load, checks and practice queries
docker-compose.yml      SQL Server 2022 in Docker, reachable from this machine only
```

## Synthetic data only

Everything here is made up. There are no real banks, people, tax file numbers, licence numbers or card numbers. Emails use reserved example domains, and the BSB prefix and business names are invented.

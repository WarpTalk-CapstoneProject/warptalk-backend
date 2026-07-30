# Billing database migrations

Add forward-only SQL migrations here as `<UTC timestamp>_<description>.sql`.
Statements may modify only the `subscription` and `public` migration-metadata schemas.
Payment, invoice, credit, usage, and subscription changes all remain owned by Billing.
Pair destructive changes with an expand/backfill/contract plan and a documented forward fix.

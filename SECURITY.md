# Security

## Reporting a vulnerability

Please do not open a public issue for a security problem. Use **Report a vulnerability** under the repository's *Security* tab (GitHub private vulnerability reporting), and include what you found, how to reproduce it and the version or commit you tested.

This is a volunteer-maintained project. Reports are handled on a best-effort basis; there is no guaranteed response time. Fixes are released for the latest version only.

## What SyncLane handles

SyncLane stores job state in your database and calls your handlers. It never sees the credentials your own handlers use to reach other systems, and it does not open a network port. Keep the following in mind when you deploy it:

- **The connection string.** It is read from your configuration and is never logged. Supply it through an environment variable or your secret store. Do not commit it.
- **Database permissions.** With `CreateSchema` on, the database user needs the right to create a schema, a table and indexes. If you would rather not grant that, turn it off, apply `SyncLaneSchema.Script()` (or [docs/schema.sql](docs/schema.sql)) yourself and give the user `USAGE` on the schema, only `SELECT`, `INSERT` and `UPDATE` on the `jobs` table and only `SELECT` on the `migrations` table.
- **The `error` column.** It holds the type and message of every exception in the chain, up to 2,000 characters, exactly as your handler threw it. If your HTTP client puts request URLs, headers or response bodies into exception messages, they end up in the table and in the log. Review what your handlers throw before you show the column to a wider audience.
- **Credentials for recipes (`SyncLane.Http`).** Your `ICredentialStore` gives an owner's credential for each page. SyncLane adds it to the request and keeps nothing: not in the table, a log, a span or a message. OAuth 2.0 access tokens are kept in memory until they expire and never written. Storing credentials encrypted, and deciding who can read them, is up to you. An API key in a query string is part of the URL, which proxies and the API's own logs record; prefer a header when the API accepts one.
- **The `owner` and `lane` columns.** They hold the identifiers you pass in. Use internal ids, not email addresses or tokens.
- **The schema name.** `Schema` is placed into SQL as an identifier. It is validated at start-up and anything that is not a plain lower-case identifier is refused. Do not build it from user input anyway.
- **Who can write to the table.** Anyone who can update the table can make your workers run a handler for any owner. Treat write access to it like access to the application itself.

## The demo

`samples/` and `docker-compose.yml` exist to try the library. The demo database accepts connections without a password, and the demo page has no login and lets anyone requeue jobs. Neither is meant to be deployed.

# Changelog

All notable changes to the public packages are documented in this file.

The format is based on Keep a Changelog, and this project follows Semantic Versioning.

## [0.9.0] - 2026-07-05

### Changed

- BREAKING: `PACK` and `ZAP` now require writers opened or created with `LockMode.Exclusive`.
  Calling either command through the default/shared writer now fails before touching the file with
  typed VFP error `110` ("File must be opened exclusively.").
- BREAKING: microVFP's maximum procedure/function call depth was tightened from `64` to `48`. Code
  that relied on deeper recursion now hits the existing maximum-call-depth error earlier.
- BREAKING: `DbfWriter.Create` (and the VFP-SQL / microVFP `CREATE TABLE` paths that route through it)
  now opens the freshly created table with `LockMode.Exclusive` by default (was `LockMode.Shared`),
  matching Visual FoxPro — `CREATE TABLE` opens the new table `EXCLUSIVE`, so a just-created table is
  immediately `PACK`/`ZAP`/`INDEX`-able (this is what makes the create → `Pack()` quickstart work with
  defaults again). Pass `DbfCreateOptions.LockMode = LockMode.Shared` explicitly when a second handle
  must coexist with the writer while it is still open (that stays fully supported); after `Dispose` the
  file is a normal on-disk table either way.

# Changelog

## 0.1.0 — 2026-11-17

- Initial public release of Scripts 0.1.0: the `scripts` command, `.scs`
  sources and `SCRIPTS-*` error codes.
- Statically typed scripting language, implemented as an interpreter shipped as
  a single Native AOT binary (no .NET runtime dependency); a `.scs` file is
  loaded, statically checked and interpreted, with no build step for the script.
- Static checker, `run`, `check`, `explain`, `fmt --check` and
  `describe --format json`, with `--errors-json` and `--json-out`.
- Frozen standard library (filesystem, processes, environment) with typed
  errors and bounded-concurrency helpers, including `process.set_umask`,
  `filesystem.make_hard_link`, `filesystem.get_file_system_status`,
  `integer.absolute` and `float.absolute`.
- `json.decode` rejects a document with a duplicate object key as
  `invalid_syntax` instead of an ambiguous last-wins map.
- Vendored the Core subset (`Results`, `Serialization.Values`, `Concurrency`)
  at Core v0.1.1 (security release) under LGPL-3.0-or-later; local JSON codecs.
- The tree is `src/`, `tests/`, `tools/scripts/` and `packaging/`; no samples.

# Changelog

## 0.1.0 — 2026-11-17

- Initial public release of Scripts 0.1.0: the `scripts` command, `.scs`
  sources and `SCRIPTS-*` error codes.
- `json.decode` rejects a document with a duplicate object key as
  `invalid_syntax` instead of an ambiguous last-wins map.
- Vendored the Core subset (`Results`, `Serialization.Values`, `Concurrency`)
  at Core v0.1.1 (security release) under LGPL-3.0-or-later; local JSON codecs.
- The tree is `src/`, `tests/`, `tools/scripts/` and `packaging/`; no samples.

# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-11-17

### Added

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

### Fixed

- `json.decode` rejects a document with a duplicate object key as
  `invalid_syntax` instead of an ambiguous last-wins map.

### Security

- Vendored the Core subset (`Results`, `Serialization.Values`, `Concurrency`)
  at Core v0.1.1 (security release) under LGPL-3.0-or-later; the JSON codecs
  are local GPL-3.0-or-later adaptations.

[Unreleased]: https://github.com/benmoussa-younes/scripts/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/benmoussa-younes/scripts/releases/tag/v0.1.0

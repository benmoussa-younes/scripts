# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.2] - 2026-11-26

### Changed

- Releases no longer carry a detached OpenPGP signature of the checksum files:
  0.1.2 had one, 0.2.0, 0.2.1 and 0.2.2 have none, and the release workflow
  produces none. It attests the provenance of every asset it uploads instead, and
  refuses to publish unless the artefacts name the signed `vX.Y.Z` tag,
  verified with the committed maintainer key. The README says what a reader can
  check with `git verify-tag` and `gh attestation verify`.

### Fixed

- `SCRIPTS-LINT-0004` no longer warns when the program's basename is written
  literally inside an interpolated string (`"{out}/sextant-vault"`): a literal
  launcher basename still warns (`SCRIPTS-LINT-0001`), and a basename that is
  not determined syntactically (`"{prefix}ash"`, `"{dir}/{name}"`) still warns
  (`0004`). The `0004` suggestion now points to a `run_request` built where the
  program is literal.
- `SCRIPTS-LINT-0004` no longer adds a warning when the call already has a type
  or arity error: `process.request(p"/bin/bash", …)` and
  `process.request(p"/bin/bash")` report `SCRIPTS-TYPE-0012`/`SCRIPTS-TYPE-0013`
  only.

## [0.2.1] - 2026-11-22

### Changed

- `scripts fmt` is renamed `scripts format`; `fmt` still runs and prints a
  deprecation warning, and will be removed in 0.3.0.
- The README is rewritten for a first-time reader: why the language exists, a
  verified quick start with its output, the install steps with their checksum,
  a security policy with private reporting, and the licence last. External
  contributions are not accepted; a fork is the supported way to build on the
  work, and no warranty is provided beyond the licences.

### Fixed

- The Debian package is built with `xz` compression (`dpkg-deb -Zxz`), so it
  installs on a dpkg older than 1.21.18 (Ubuntu 22.04) and is identical across
  build hosts.

## [0.2.0] - 2026-11-21

### Added

- `process.exit_with_message(exit_code, message)`: exit with a message on
  stderr (`SCRIPTS-RUNTIME-0016` for a code outside 1..255).
- `result<unit, E>.ignore_error()`, the explicit way to drop a cleanup failure
  (`SCRIPTS-TYPE-0071` on a result that carries a value).
- A block as a `match` arm expression (`=> { …; value }`, `SCRIPTS-TYPE-0072`
  when it does not end with a value; `SCRIPTS-TYPE-0073` for `break`,
  `continue` or `return` leaving a lambda).
- Warnings `SCRIPTS-LINT-0013` (binding never read, fixed by `check --fix`)
  and `SCRIPTS-LINT-0014` (two paths passed by position, `check --fix` names
  them); `SCRIPTS-PARSE-0059` explains that `{{` is not an escape and
  `check --fix` applies syntax fixes on the entry script.
- Numeric and math methods: `integer.power`, `integer.clamp`, `integer.minimum`,
  `integer.maximum`; `float.floor`, `float.ceiling`, `float.round`,
  `float.truncate`, `float.power`, `float.square_root`, `float.natural_log`,
  `float.log2`, `float.log10`, `float.exponential`, `float.clamp`,
  `float.minimum`, `float.maximum`; `list<float>.sum_floats`.
- `string.find_all_regex(pattern)` (NonBacktracking engine),
  `encoding.url_encode`/`encoding.url_decode` (RFC 3986, strict UTF-8),
  `list<T>.chunked(size)`, and intrinsic `csv` module (`csv.parse`,
  `csv.parse_row`, `csv.encode_row`, `csv.encode`, RFC 4180).

### Changed

- **Breaking (syntax):** a `{` after `=>` in a `match` now opens a block, so a
  map literal arm is written `({ … })`; `SCRIPTS-PARSE-0023` names the
  parenthesised form. A block arm of a `match` expression takes the value of
  its last line (`SCRIPTS-TYPE-0072` when it has none).
- Updated pinned .NET SDK to `10.0.404` (Native AOT runtime and ILCompiler
  `10.0.15`), which carries the November 2026 .NET runtime security fixes,
  among them `CVE-2026-109475` (denial of service) and `CVE-2026-105544` /
  `CVE-2026-114155` (elevation of privilege). `CVE-2026-118747` is in the
  standalone `System.Formats.Cbor` package, which this project does not
  reference.

### Removed

- Removed `process.run_shell` (moved to `RetiredNames`, rejected with
  `SCRIPTS-TYPE-0068` pointing to `process.run(process.request(program, arguments))`,
  and retired `SCRIPTS-LINT-0002`).

### Fixed

- `parallel.run_all` and process waits could hang forever after a lost wake-up
  caused by concurrent `Dispose` on wait handles.
- A `SIGINT` or `SIGHUP` ignored when `scripts` started (a background job,
  `nohup`) is kept ignored, per POSIX convention: no handler is installed, so
  the inherited disposition is not overridden.
- The `system.get_information` catalogue entry named itself instead of
  `uname(2)`.

## [0.1.2] - 2026-11-17

### Changed

- Make the release gate portable on GitHub Actions: the parallel promptness
  bound follows the runner's CPU count, and the spawn descriptor probe
  duplicates a low descriptor above stdio. No language or binary behaviour
  change.

## [0.1.1] - 2026-11-17

### Fixed

- Treat a descriptor 0 opened by the .NET runtime with `FD_CLOEXEC` as an
  absent standard input, so `scripts run -` and `check -` with a closed
  standard input remain an empty script on hosts where the runtime leaves a
  regular file on descriptor 0 (GitHub Actions); this removes the 251
  static-error regression.

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

[0.2.1]: https://github.com/benmoussa-younes/scripts/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/benmoussa-younes/scripts/releases/tag/v0.2.0
[0.1.2]: https://github.com/benmoussa-younes/scripts/releases/tag/v0.1.2
[0.1.1]: https://github.com/benmoussa-younes/scripts/releases/tag/v0.1.1
[0.1.0]: https://github.com/benmoussa-younes/scripts/releases/tag/v0.1.0

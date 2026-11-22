# scripts — a statically typed language for Linux automation

[![CI](https://github.com/benmoussa-younes/scripts/actions/workflows/ci.yml/badge.svg)](https://github.com/benmoussa-younes/scripts/actions/workflows/ci.yml)

`scripts` is a statically typed scripting language for Linux system automation,
shipped as one native executable (Native AOT, .NET 10) with no runtime to
install. A `.scs` file is loaded, statically checked and interpreted; there is
no build step for the script.

## Contents

- [Why scripts](#why-scripts)
- [Status](#status)
- [Install](#install)
- [Quick start](#quick-start)
- [Commands](#commands)
- [Security](#security)
- [Build and test](#build-and-test)
- [Layout](#layout)
- [Documentation](#documentation)
- [Contributing and warranty](#contributing-and-warranty)
- [Changelog](#changelog)
- [Licence](#licence)

## Why scripts

- **Data is never re-parsed as code.** A program is launched only through an
  explicit request, `process.run(process.request(program, arguments))`, never a
  shell string, so the injection class of shell scripts disappears.
- **Static types, explicit failures.** `result<T, E>` and `?` propagate errors;
  the checker rejects a discarded value and an unreachable branch, and warns on
  a binding that is never read and on two paths passed by position.
- **A typed standard library.** Files, processes, environment, JSON, CSV,
  encoding, regular expressions (the `NonBacktracking` engine) and bounded
  parallelism, each with typed errors.
- **One binary, no runtime.** The Native AOT executable links only against
  libc and libm.
- **A language description for machines.** `scripts describe --format json` is
  the authoritative reference, for tools and agents as well as humans.

## Status

Scripts 0.2.1 is the current release. The language is usable; as a
`0.y.z` release its API may still change. Supported platform: Linux x86-64,
glibc 2.34 or newer.

## Install

Requirements: Linux x86-64, glibc >= 2.34. The binary links only against libc
and libm; no .NET runtime is needed at run time.

Download the assets from the
[latest release](https://github.com/benmoussa-younes/scripts/releases/latest)
and verify them with `sha256sum -c SHA256SUMS`. The checksum proves integrity,
not origin. Releases carry no detached OpenPGP signature of it: 0.1.2 had one,
0.2.0 and 0.2.1 have none, and the release workflow produces none.

Origin rests on two facts. The `vX.Y.Z` tag is signed by the maintainer key,
whose public half is `packaging/maintainer-key.asc`, with the fingerprint
`F3955A21FE39065BC6F1433203911AA81394689F`:

```sh
gpg --import packaging/maintainer-key.asc
git verify-tag vX.Y.Z
```

And a release published after 0.2.1 carries a build provenance attestation for
every asset the release workflow uploads:

```sh
gh attestation verify scripts_<version>_amd64.deb \
  --repo benmoussa-younes/scripts \
  --signer-workflow benmoussa-younes/scripts/.github/workflows/release.yml
```

### Debian/Ubuntu package

```sh
sudo apt install ./scripts_0.2.1_amd64.deb
scripts --version
```

### Binary tarball

```sh
tar -xzf scripts_0.2.1_linux-x64.tar.gz
sudo install -m 755 scripts-0.2.1/scripts /usr/local/bin/scripts
```

### From source

Build requirements: .NET SDK 10.0.404 (see `global.json`), clang, zlib1g-dev.

```sh
dotnet publish src/Scripts.App/Scripts.App.csproj -c Release -r linux-x64
# binary: src/Scripts.App/bin/Release/net10.0/linux-x64/publish/scripts
```

## Quick start

`hello.scs`:

```scs
io.write_standard_output_line("hello from scripts")
```

```sh
scripts run hello
# hello from scripts
```

`scripts run hello` resolves `hello.scs`. With a shebang
(`#!/usr/bin/env -S scripts run`) and the executable bit, `./hello.scs` also
runs it.

A typed example, `lines.scs`, reads a file, propagates the failure with `?` and
matches the result:

```scs
function count_lines(file_path: path) -> result<integer, error> {
    let content = filesystem.read_file(file_path)?
    let trimmed = content.trim_whitespace()
    if trimmed.length() == 0 {
        return ok(0)
    }
    return ok(trimmed.split("\n").length())
}

match count_lines(p"notes.txt") {
    ok(lines) => io.write_standard_output_line("{lines} line(s)")
    error(failure) => process.exit_with_message(1, "{failure.kind}: {failure.message}")
}
```

```sh
scripts run lines
# 2 line(s)
```

Launching a program is a request, never a shell string:

```scs
let outcome = process.run(process.request("git", ["status", "--short"]))?
io.write_standard_output_line(outcome.standard_output)
```

## Commands

| Command | What it does |
|---|---|
| `scripts run <file> [argument…]` | check, then run; arguments are exposed as `process.arguments` |
| `scripts check <file>` | static analysis without running; `--fix` applies the safe fixes |
| `scripts explain <file>` | check, then explain each statement in English |
| `scripts format --check <file>` | report whether the layout is canonical |
| `scripts describe --format json` | the machine-readable language reference |
| `scripts --version`, `scripts --help` | version and usage |

## Security

- No secret belongs in the tree, in a fixture or in a log.
- Data is never re-parsed as code: there is no shell execution path,
  `process.run_shell` was removed in 0.2.0, and regular expressions use the
  `NonBacktracking` engine.
- Text decoding is strict UTF-8; `check --fix` keeps the `O_NOFOLLOW` and BOM
  guards.
- Report a vulnerability privately through
  [GitHub private vulnerability reporting](https://github.com/benmoussa-younes/scripts/security/advisories/new);
  never in a public issue.

## Build and test

```sh
dotnet build Scripts.slnx -c Release
bash tools/scripts/binaries/gate
```

Tests run only as Native AOT executables, published by
`tools/scripts/binaries/aot-tests.sh`; there is no `dotnet test` path. The gate
runs `check-all` with the build (6 steps: build, AOT app, helpers, packaging
metadata, AOT tests, licences); `check-all --no-build` runs the 5 remaining
steps (the AOT publishes still compile). It prints one `VERDICT` line and does
not change tracked files. `gate-static` is the lighter gate. `reuse` 5.0.2 is
needed for the licence step.

`reuse lint` scans the whole tree; the undecodable-name fixture used by the
filesystem corpus is generated at test time.

## Layout

- `src/`: the lexer, parser and static checker, the interpreter/runtime, the
  CLI and the vendored Core subset.
- `tests/`: the xUnit projects, published and run as Native AOT executables;
  `Scripts.Corpus.Tests` runs the `Corpus/` data (base, stdlib, errors,
  explain, stdin, migration, differential, json, describe) and the four
  black-box axes (fuzz, semantics, JSON, system).
- `tools/scripts/`: the gate, the release-metadata check and the helpers.
- `packaging/`: the Debian package and the source and binary tarballs.

## Documentation

- [`AGENTS.md`](AGENTS.md): how the repository is built, tested and released.
- `scripts describe --format json`: the complete language and standard library.
- `scripts explain <file>`: an explanation of one script.
- [`CHANGELOG.md`](CHANGELOG.md): notable changes, per release.

## Contributing and warranty

External contributions are **not accepted**; unsolicited pull requests or
patches may be closed without review. A fork is the supported way to build on
this work; the maintainer workflow is in [`AGENTS.md`](AGENTS.md). The project
is maintained by one author: no support contract, service level or warranty,
express or implied, is provided beyond the licences.

## Changelog

See [`CHANGELOG.md`](CHANGELOG.md); the project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Licence

Scripts is GPL-3.0-or-later. The vendored Core subset (`Results`,
`Serialization.Values`, `Concurrency`) under
`src/Scripts.Core/Compatibility/Core/` is LGPL-3.0-or-later; the local JSON
codecs are GPL-3.0-or-later; the .NET 10 runtime components are MIT. The prose
documents (README, AGENTS, CHANGELOG, THIRD-PARTY) are CC-BY-SA-4.0. See
[`LICENSE`](LICENSE), [`LICENSES/`](LICENSES/), [`LICENSE.DOTNET`](LICENSE.DOTNET)
and [`THIRD-PARTY`](THIRD-PARTY) for the exact texts and provenance.

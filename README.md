# scripts — a statically typed programming language for Linux

`scripts` is a statically typed scripting language for Linux system automation.
Its reference implementation is an interpreter shipped as one native executable
(Native AOT, .NET 10): no runtime to install. A `.scs` file is loaded, statically
checked and interpreted; there is no build step for the script. It keeps the
source as plain `.scs` files, with direct access to files and processes, and
removes the cause of most shell bugs: data silently re-parsed as code. It is
designed for humans and agents.

## Status

Scripts 0.1.0 is the initial public release. The language is usable; as a
`0.y.z` release its API may still change. Supported platform: Linux x86-64,
glibc 2.34 or newer.

## Install

Requirements: Linux x86-64, glibc >= 2.34. The binary links only against libc
and libm; no .NET runtime is needed at run time.

Debian/Ubuntu package:

```sh
sudo apt install ./scripts_0.1.0_amd64.deb
scripts --version
```

Binary tarball:

```sh
tar -xzf scripts_0.1.0_linux-x64.tar.gz
sudo install -m 755 scripts-0.1.0/scripts /usr/local/bin/scripts
```

From source (build requirements: .NET 10 SDK, clang, zlib1g-dev):

```sh
dotnet publish src/Scripts.App/Scripts.App.csproj -c Release -r linux-x64
# binary: src/Scripts.App/bin/Release/net10.0/linux-x64/publish/scripts
```

## Quick start

`hello.scs`:

```scs
io.write_standard_output_line("hello from scripts")
```

Run it:

```sh
scripts run hello
```

`scripts run hello` resolves `hello.scs`. With a shebang
(`#!/usr/bin/env -S scripts run`) and the executable bit, `./hello.scs` also runs it.

## Commands

- `scripts run <file>`: check, then run a script;
- `scripts check <file>`: static analysis without running;
- `scripts explain <file>`: explain a script;
- `scripts fmt --check <file>`: check canonical layout;
- `scripts describe --format json`: machine-readable language reference;
- `scripts --help`, `scripts help`: command-line help.

`scripts describe --format json` is the authoritative reference for the
language and the standard library; `scripts explain` explains a source file.

## Build and test

```sh
dotnet build Scripts.slnx -c Release
bash tools/scripts/binaries/gate
```

Tests run only as Native AOT executables, published by
`tools/scripts/binaries/aot-tests.sh`; there is no `dotnet test` path. The gate
runs `check-all` with the build (5 steps: build, AOT app, helpers, AOT tests,
licences); `check-all --no-build` runs the 4 remaining steps (the AOT publishes
still compile). It prints one `VERDICT` line and does not change tracked files.
`gate-static` is the lighter worker gate. `reuse` 5.0.2 is needed for the
licence step.

`reuse lint` scans the whole tree; the undecodable-name fixture used by the
filesystem corpus is generated at test time, not tracked.

## Layout

- `src/`: the parser and static checker, the interpreter/runtime, the CLI and
  the vendored Core subset.
- `tests/`: the xUnit projects (published and run as Native AOT executables),
  `Scripts.Corpus.Tests`, and the `Corpus/` data (base, stdlib, errors,
  explain, stdin, migration, differential, json, describe). The four black-box
  axes (fuzz, semantics, JSON, system) run in `Scripts.Corpus.Tests`; no shell
  test runner remains (the migration fixtures keep their `.sh` scripts).
- `tools/scripts/`: the gate and verifiers.
- `packaging/`: the Debian package and the source and binary tarballs.

## Contributing

Issues and pull requests are welcome. Before sending a change, run
`bash tools/scripts/binaries/gate`; a change lands only with a green gate and
its tests. Keep the language small: a new feature needs a failing test, its
runtime/corpus proof, and an entry in `scripts describe` when it is part of the
standard library.

## Changelog

See [CHANGELOG.md](CHANGELOG.md); the project follows
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Licence

Scripts is GPL-3.0-or-later. The vendored Core subset (`Results`,
`Serialization.Values`, `Concurrency`) under
`src/Scripts.Core/Compatibility/Core/` is LGPL-3.0-or-later; the local JSON
codecs are GPL-3.0-or-later; the .NET 10 runtime components are MIT. The prose
documents (README, AGENTS, CHANGELOG, THIRD-PARTY) are CC-BY-SA-4.0. See
`LICENSE`, `LICENSES/`, `LICENSE.DOTNET` and `THIRD-PARTY` for the exact texts
and provenance.

## Reference

`scripts describe --format json` is the machine-readable reference,
`scripts explain <file>` explains a source file, and `scripts --help` lists the
command line.

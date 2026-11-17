# scripts — a typed scripting language for Linux

`scripts` is a statically typed language for Linux system automation, compiled
with .NET 10 Native AOT into one native executable: no runtime to install. It
keeps one file, no build step, direct access to files and processes, and removes
the cause of most shell bugs: data silently re-parsed as code. It is designed for
humans and agents.

## 10-second demo

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

## Build

```sh
dotnet build Scripts.slnx -c Release
dotnet publish src/Scripts.App/Scripts.App.csproj -c Release -r linux-x64
```

Tests run only as Native AOT executables, published by
`tools/scripts/binaries/aot-tests.sh`; there is no `dotnet test` path. The gate
is `tools/scripts/binaries/gate`; it runs `check-all` with the build
(5 steps: build, AOT app, helpers, AOT tests, licences).
`check-all --no-build` runs the 4 remaining steps (the AOT publishes still
compile). The gate prints one `VERDICT` line and does not change tracked
files. The agent gate is a script for the `bcs` 0.5.0 interpreter used as
bootstrap; without bcs, `tools/scripts/binaries/gate.sh` runs the same
`check-all` and prints the same single `VERDICT`. The product is `scripts`.
`reuse` 5.0.2 is needed for the licence step.

`reuse lint` scans the whole tree; the undecodable-name fixture used by the
filesystem corpus is generated at test time, not tracked.

## Layout

- `src/`: the compiler, checker, runtime, CLI and the vendored Core subset.
- `tests/`: the xUnit projects (published and run as Native AOT executables),
  `Scripts.Corpus.Tests`, and the `Corpus/` data (base, stdlib, errors,
  explain, stdin, migration, differential, json, describe). The four black-box
  axes (fuzz, semantics, JSON, system) run in `Scripts.Corpus.Tests`; no shell
  corpus remains.
- `tools/scripts/`: the gate and verifiers.
- `packaging/`: the Debian package and the source and binary tarballs.
- `docs/`: product documentation; the agent plan and kanban live in
  `~/agents/projects/scripts/`.

## Licence

Scripts is GPL-3.0-or-later. The vendored Core subset (`Results`,
`Serialization.Values`, `Concurrency`) under `src/Scripts.Core/Compatibility/Core/`
is LGPL-3.0-or-later; the local JSON codecs are GPL-3.0-or-later. The prose
documents (README, AGENTS, CHANGELOG, NOTICE, THIRD-PARTY) are CC-BY-SA-4.0;
see `LICENSE`,
`LICENSES/`, `NOTICE` and `THIRD-PARTY` for the exact texts and provenance.

## Reference

`scripts describe --format json` is the machine-readable reference,
`scripts explain <file>` explains a source file, and `scripts --help` lists the
command line.

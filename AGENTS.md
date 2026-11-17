# AGENTS.md — Scripts

A short `AGENTS.md` may exist in a subfolder when that folder has important LLM
rules. Keep it to a few lines; the closest file wins.

## Build and test

```sh
dotnet build Scripts.slnx -c Release
reuse lint
```

Tests run only as Native AOT executables (`tools/scripts/binaries/aot-tests.sh`,
six projects); there is no `dotnet test`/VSTest path. The gate is
`tools/scripts/binaries/gate`; it runs `check-all` with the build (5 steps:
build, AOT app, helpers, AOT tests, licences) and prints one `VERDICT` line;
`check-all --no-build` runs 4 (the AOT publishes still compile). The gate is a
bash script; no `bcs` bootstrap is required. `tools/scripts/binaries/gate-static` is the
lighter worker gate (build, fast AOT tests, tree hygiene). None changes tracked
files. The product is `scripts`.

`reuse lint` scans the whole tree; the undecodable-name fixture used by the
filesystem corpus is generated at test time, not tracked.

## Repository facts

- Root markdown is limited to `README.md`, `AGENTS.md` and `CHANGELOG.md`; the
  agent plan and kanban live in `~/agents/projects/scripts/`; one root
  `AGENTS.md`.
- Scripts is GPL-3.0-or-later; the vendored Core subset (`Results`,
  `Serialization.Values`, `Concurrency`) under `src/Scripts.Core/Compatibility/Core/`
  is LGPL-3.0-or-later, and the local JSON codecs are GPL-3.0-or-later. The
  prose documents (README, AGENTS, CHANGELOG, NOTICE, THIRD-PARTY) are
  CC-BY-SA-4.0; see `NOTICE`,
  `THIRD-PARTY` and `LICENSES/`.
- The command is `scripts`; sources are `.scs`; error codes are `SCRIPTS-*`.
- The tree is `src/`, `tests/`, `tools/scripts/` and `packaging/`; no samples.
- `tests/Scripts.Corpus.Tests` runs the corpus in xUnit; its data lives under
  `tests/Corpus`. The four black-box axes (fuzz, semantics, JSON, system) run
  in the same project; no shell test runner remains (the migration fixtures keep
  their `.sh` scripts). Every test project is published as a Native AOT
  executable and run directly.
- Scripts is consumed as source; no NuGet package is produced.

## Maintenance

- Keep the version and `CHANGELOG.md` up to date after every release.

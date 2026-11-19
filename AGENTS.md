# AGENTS.md — Scripts

A statically typed scripting language for Linux system automation, shipped as
one native executable (Native AOT, .NET 10). A `.scs` file is loaded, statically
checked and interpreted; there is no build step for the script, and data is
never re-parsed as code.

## Build and test

```sh
dotnet build Scripts.slnx -c Release
reuse lint
tools/scripts/binaries/gate            # check-all: build, AOT app, helpers, AOT tests, licences
tools/scripts/binaries/gate-static     # the lighter worker gate: build, fast AOT tests, tree hygiene
```

Tests run only as Native AOT executables (`tools/scripts/binaries/aot-tests.sh`,
six projects); there is no `dotnet test`/VSTest path. `check-all --no-build`
skips the build step. The gate prints one `VERDICT` line and does not change
tracked files. `reuse lint` scans the whole tree; the undecodable-name fixture is
generated at test time.

## Conventions

- Sources are `.scs`; the command is `scripts`; error codes are `SCRIPTS-*`.
- A program is a literal: a computed program breaks the launcher check
  (`SCRIPTS-LINT-0004`) and the argument discipline.
- The tree is `src/`, `tests/`, `tools/scripts/` and `packaging/`; no samples.
- `tests/Scripts.Corpus.Tests` runs the corpus in xUnit, with the four
  black-box axes (fuzz, semantics, JSON, system) in the same project.
- A subfolder may carry a short `AGENTS.md`; the closest file wins.

## Repository facts

- Scripts is `GPL-3.0-or-later`; the vendored Core subset under
  `src/Scripts.Core/Compatibility/Core/` is `LGPL-3.0-or-later`; the .NET 10
  runtime components are MIT (`LICENSE.DOTNET`, `THIRD-PARTY-DOTNET.txt`).
- The prose documents (README, AGENTS, CHANGELOG, THIRD-PARTY) are
  `CC-BY-SA-4.0`; see `THIRD-PARTY` and `LICENSES/`.
- Scripts is consumed as source; no NuGet package is produced.
- Root markdown is limited to `README.md`, `AGENTS.md` and `CHANGELOG.md`.

## Commits and releases

- One branch per task, `work/<slug>`; one concern per commit; English,
  imperative subject; the body says why.
- Commits and tags are GPG-signed. The maintainer merges a green `check`
  locally; publishing (push, tag, release) is the owner's act.

## Security

- No secret and no consumer's data in the tree or in a fixture.
- A vulnerability report is private: follow `SECURITY.md`.

## Maintenance

- Keep the version and `CHANGELOG.md` up to date after every release.

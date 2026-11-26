#!/usr/bin/env bash
# aot-tests.sh — run the xUnit suites as Native AOT executables.
#
# Usage: aot-tests.sh [--all|--fast]
#   --all  (default) Syntax, Checker, Cli, EndToEnd, Runtime, Corpus.
#   --fast Syntax, Checker and Cli only: the worker gate (no app spawn).
#
# Every project is published with `dotnet publish -r linux-x64` (PublishAot is
# set in tests/Directory.Build.props) and the produced executable is run
# directly. There is no VSTest host and no `dotnet test` path: a test that only
# passes in JIT is not a pass here.
#
# The E2E and corpus suites receive the published Native AOT app binary through
# SCRIPTS_E2E_BINARY / SCRIPTS_CORPUS_BINARY.
set -u
export LC_ALL=C
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

mode=all
for argument in "$@"; do
  case "$argument" in
    --all) mode=all ;;
    --fast) mode=fast ;;
    *)
      echo "usage: aot-tests.sh [--all|--fast]" >&2
      exit 2
      ;;
  esac
done

script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "$script_directory/../../.." && pwd)"
cd "$repository_root" || exit 1

app_binary="$repository_root/src/Scripts.App/bin/Release/net10.0/linux-x64/publish/scripts"

fast_projects=(
  Scripts.Syntax.Tests
  Scripts.Checker.Tests
  Scripts.Cli.Tests
)
all_projects=(
  Scripts.Syntax.Tests
  Scripts.Checker.Tests
  Scripts.Cli.Tests
  Scripts.EndToEnd.Tests
  Scripts.Runtime.Tests
  Scripts.Corpus.Tests
)

if [ "$mode" = "fast" ]; then
  projects=("${fast_projects[@]}")
else
  projects=("${all_projects[@]}")
  if [ ! -x "$app_binary" ]; then
    echo "== AOT app: dotnet publish src/Scripts.App -r linux-x64"
    dotnet publish src/Scripts.App/Scripts.App.csproj -c Release -r linux-x64 -m:1 || exit 1
  fi
fi

for project in "${projects[@]}"; do
  echo "== $project: dotnet publish (Native AOT)"
  dotnet publish "tests/$project/$project.csproj" -c Release -r linux-x64 -m:1 || exit 1
  executable="tests/$project/bin/Release/net10.0/linux-x64/publish/$project"
  if [ ! -x "$executable" ]; then
    echo "error: AOT test executable not found: $executable" >&2
    exit 1
  fi
  echo "== $project: run"
  SCRIPTS_E2E_BINARY="$app_binary" \
    SCRIPTS_CORPUS_BINARY="$app_binary" \
    "$executable" || exit 1
done

echo "aot-tests: PASS (${#projects[@]} projects, Native AOT)"

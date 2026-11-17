#!/usr/bin/env bash
# Complete orchestration of the scripts checks.
#
# Usage: check-all.sh [--no-build]
#   --no-build : skip the redundant `dotnet build`; the AOT publishes still compile.
#
# Runs, IN ORDER, every proof mechanism of the repository, stops at the first
# failure and prints a PASS/FAIL summary per step:
#   1. build                dotnet build Scripts.slnx -c Release
#   2. AOT app              dotnet publish src/Scripts.App -r linux-x64
#   3. script helpers       scripts run tools/scripts/binaries/test-scripts.scs
#   4. AOT tests            publish + run the six Native AOT xUnit executables
#   5. licences             reuse lint on the tree
#
# Tests only run as Native AOT executables: there is no `dotnet test` step and
# no VSTest host. The step count is asserted below: a silently skipped corpus
# (or a step that disappeared from the script) fails the gate instead of
# shrinking it.
#
set -u
export LC_ALL=C
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

do_build=1
for argument in "$@"; do
  case "$argument" in
    --no-build) do_build=0 ;;
    *)
      echo "usage: check-all.sh [--no-build]" >&2
      exit 2
      ;;
  esac
done

# Five steps with the build, four without it (the AOT app publish always runs).
expected_steps=5
if [ "$do_build" -eq 0 ]; then
  expected_steps=4
fi

script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "$script_directory/../../.." && pwd)"
cd "$repository_root"

# The product binary the tests exercise: the Native AOT deliverable, never the
# JIT apphost under bin/Release/net10.0/.
scripts_binary="$repository_root/src/Scripts.App/bin/Release/net10.0/linux-x64/publish/scripts"
# Results per step (for the final summary).
step_names=()
step_results=()

run_step() {
  local name="$1"
  shift
  step_names+=("$name")
  echo
  echo "==================================================================="
  echo "== $name"
  echo "==================================================================="
  if "$@"; then
    step_results+=("PASS")
    echo "--- $name : PASS"
    return 0
  else
    local status=$?
    step_results+=("FAIL")
    echo "--- $name : FAIL (code $status)"
    return "$status"
  fi
}

print_summary() {
  echo
  echo "==================================================================="
  echo "== tools/scripts/binaries/check-all.sh summary"
  echo "==================================================================="
  local index
  for index in "${!step_names[@]}"; do
    printf '%-6s %s\n' "${step_results[$index]}" "${step_names[$index]}"
  done
}

fail_fast() {
  print_summary
  echo
  echo "check-all : FAIL (stopped at step « $1 »)"
  exit 1
}

# Every test project is part of the gate: a missing project is a failure, never
# a silent skip.
for required_runner in \
  tests/Scripts.Syntax.Tests/Scripts.Syntax.Tests.csproj \
  tests/Scripts.Checker.Tests/Scripts.Checker.Tests.csproj \
  tests/Scripts.Cli.Tests/Scripts.Cli.Tests.csproj \
  tests/Scripts.EndToEnd.Tests/Scripts.EndToEnd.Tests.csproj \
  tests/Scripts.Runtime.Tests/Scripts.Runtime.Tests.csproj \
  tests/Scripts.Corpus.Tests/Scripts.Corpus.Tests.csproj; do
  if [ ! -f "$required_runner" ]; then
    echo "error: missing test project: $required_runner" >&2
    echo "check-all : FAIL (missing $required_runner)"
    exit 1
  fi
done

# 1. Build.
if [ "$do_build" -eq 1 ]; then
  run_step "build (dotnet build Scripts.slnx -c Release)" \
    dotnet build Scripts.slnx -c Release || fail_fast "build"
fi

# 2. Native AOT app: the binary every test exercises.
run_step "AOT app (dotnet publish src/Scripts.App -r linux-x64)" \
  dotnet publish src/Scripts.App/Scripts.App.csproj -c Release -r linux-x64 -m:1 \
  || fail_fast "AOT app"

if [ ! -x "$scripts_binary" ]; then
  step_names+=("AOT app binary present")
  step_results+=("FAIL")
  echo "error: AOT binary not found: $scripts_binary" >&2
  fail_fast "AOT app binary present"
fi

# The AOT binary must carry the version of the tree it was built from.
expected_version="$(sh tools/scripts/binaries/version.sh)"
actual_version="$("$scripts_binary" --version | sed 's/^scripts //')"
if [ "$actual_version" != "$expected_version" ]; then
  step_names+=("AOT binary version matches the tree")
  step_results+=("FAIL")
  echo "error: AOT binary version $actual_version, tree version $expected_version" >&2
  fail_fast "AOT binary version matches the tree"
fi

# 3. Script helpers (version computed from git, 0.1.0 fallback).
run_step "script helpers (version fallback)" \
  "$scripts_binary" run tools/scripts/binaries/test-scripts.scs || fail_fast "script helpers"

# 4. AOT tests: each xUnit project is published as a Native AOT executable and
#    run directly by aot-tests.sh (E2E and corpus receive the AOT app binary).
run_step "AOT tests (xUnit Native AOT)" \
  bash tools/scripts/binaries/aot-tests.sh --all || fail_fast "AOT tests"

# 5. Licences: `reuse lint` on the tracked tree.
run_licences() {
  local work list status
  work="$(mktemp -d)" || return 1
  list="$(mktemp)" || { rm -rf "$work"; return 1; }
  if ! git ls-files -z --cached --others --exclude-standard > "$list"; then
    rm -rf "$work" "$list"
    return 1
  fi
  while IFS= read -r -d '' file; do
    # A staged deletion (the owner has not committed yet) is not a file to lint.
    if [ ! -e "$file" ] && [ ! -L "$file" ]; then
      continue
    fi
    mkdir -p "$work/$(dirname "$file")" || { rm -rf "$work" "$list"; return 1; }
    cp -a -- "$file" "$work/$file" || { rm -rf "$work" "$list"; return 1; }
  done < "$list"
  rm -f "$list"
  ( cd "$work" && reuse lint )
  status=$?
  rm -rf "$work"
  return $status
}
run_step "licences (reuse lint)" run_licences || fail_fast "licences"

if [ "${#step_names[@]}" -ne "$expected_steps" ]; then
  print_summary
  echo
  echo "check-all : FAIL (expected $expected_steps steps, ran ${#step_names[@]})" >&2
  exit 1
fi

print_summary
echo
echo "check-all : PASS (${#step_names[@]}/$expected_steps steps)"
exit 0

#!/usr/bin/env bash
# Public gate without the bcs bootstrap: runs check-all.sh and prints one VERDICT.
# The agent gate (tools/scripts/binaries/gate) remains the bcs 0.5.0 script.
# Usage: gate.sh [--no-build]
set -u
export LC_ALL=C

script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "${script_directory}/../../.." && pwd)"

bash "${script_directory}/check-all.sh" "$@"
status=$?
if [ "${status}" -eq 0 ]; then
    echo "VERDICT: PASS"
    exit 0
fi

echo "VERDICT: FAIL"
exit 1

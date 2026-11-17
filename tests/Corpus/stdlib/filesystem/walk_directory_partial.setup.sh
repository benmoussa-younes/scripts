#!/usr/bin/env bash
# The undecodable-name fixture is generated, not tracked: a raw 0xFF byte in a
# file name is not valid UTF-8 and would make `reuse lint` fail on the raw tree.
set -eu
mkdir -p report-tree/sub
printf 'x' > $'report-tree/sub/bad-\xff.txt'

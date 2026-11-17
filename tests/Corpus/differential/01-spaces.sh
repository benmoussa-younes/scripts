#!/usr/bin/env bash
# bash → an argument containing spaces is split into several words
# when the expansion is not quoted (word splitting, IFS).
export LC_ALL=C

value="bonjour le monde"
# Deliberately unquoted: bash splits into 4 arguments.
printf '[%s]\n' $value
# The same call, correctly quoted, for comparison.
printf '[%s]\n' "$value"

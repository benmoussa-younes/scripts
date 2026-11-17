#!/usr/bin/env bash
# bash → the quotes contained in a value take part in the syntax
# when the expansion is not quoted: they can start a new quotation.
export LC_ALL=C

value='il dit "bonjour"'
# Deliberately unquoted: the internal quotes are reinterpreted.
printf '[%s]\n' $value
echo "---"
printf '[%s]\n' "$value"

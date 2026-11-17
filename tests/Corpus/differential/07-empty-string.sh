#!/usr/bin/env bash
# bash → an unquoted empty variable disappears; the "empty" argument is lost
# and the number of arguments changes (here printf only receives the format).
export LC_ALL=C

empty_value=""
# Deliberately unquoted: the empty argument disappears completely.
printf 'arg_count=%s\n' $empty_value
echo "---"
printf 'quoted=[%s]\n' "$empty_value"

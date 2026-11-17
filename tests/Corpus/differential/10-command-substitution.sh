#!/usr/bin/env bash
# bash → "$(…)" captures a command's output and SPLITS it into words
# when it is not quoted; bash also removes trailing \n.
export LC_ALL=C

captured=$(printf 'a b\nc\n')
printf 'capture=[%s]\n' $captured
echo "---"
printf 'longueur=%s\n' "${#captured}"

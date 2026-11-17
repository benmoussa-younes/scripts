#!/usr/bin/env bash
# bash → an argument containing a newline is split into several words
# (word splitting also treats \n when the expansion is not quoted).
export LC_ALL=C

multi_line="ligne un
ligne deux"
# Deliberately unquoted: bash makes two arguments of it.
printf '[%s]\n' $multi_line

#!/usr/bin/env bash
# bash → the length of a string depends on the locale (bytes under LC_ALL=C),
# and accented characters can be altered by splitting.
export LC_ALL=C

accented="été"
printf 'texte=[%s]\n' "$accented"
# Under LC_ALL=C, ${#var} counts BYTES (4 for "été" in UTF-8), not characters.
printf 'longueur=%s\n' "${#accented}"

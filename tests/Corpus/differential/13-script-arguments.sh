#!/usr/bin/env bash
# bash → the script's arguments must be re-quoted at each use;
# an unquoted "$*" re-splits the arguments, and $# depends on that quoting.
export LC_ALL=C

printf 'nombre=%s\n' "$#"
for received_argument in "$@"; do
  printf 'arg=[%s]\n' "$received_argument"
done
# "$*" joins everything into a single word separated by the first character of IFS.
printf 'etoile=[%s]\n' "$*"
# Unquoted "$*" is then re-split by word splitting.
printf 'recoupe=[%s]\n' $*

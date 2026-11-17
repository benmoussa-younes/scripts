#!/usr/bin/env bash
# bash → the glob "*" is expanded by the shell into local file names,
# even when it was meant to be literal data.
export LC_ALL=C

temporary_directory=$(mktemp -d)
printf 'contenu\n' > "$temporary_directory/alpha.txt"
printf 'contenu\n' > "$temporary_directory/beta.txt"
cd "$temporary_directory" || exit 1

pattern="*"
# Deliberately unquoted: the * is expanded into alpha.txt beta.txt.
printf '[%s]\n' $pattern

cd / || exit 1
rm -rf "$temporary_directory"

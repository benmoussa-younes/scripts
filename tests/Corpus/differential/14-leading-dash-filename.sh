#!/usr/bin/env bash
# bash → a file name starting with "-" is taken for an option
# by programs that did not receive "--": the command fails or acts
# on something other than the intended file.
export LC_ALL=C

temporary_directory=$(mktemp -d)
printf 'donnee\n' > "$temporary_directory/-rf"
cd "$temporary_directory" || exit 1

# Relative name "-rf" without "--": cat reads it as the options -r -f.
cat -rf 2>&1
echo "code=$?"

cd / || exit 1
rm -rf "$temporary_directory"

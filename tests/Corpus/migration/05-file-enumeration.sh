#!/usr/bin/env bash
# bash → Pattern 05: for f in *.txt and find (file enumeration)
export LC_ALL=C

temporary_directory=$(mktemp -d)
mkdir -p "$temporary_directory/sub/deep"
printf 'a\n' > "$temporary_directory/a.txt"
printf 'b\n' > "$temporary_directory/b.txt"
printf '' > "$temporary_directory/c.log"
printf '' > "$temporary_directory/.hidden.txt"
printf 'd' > "$temporary_directory/sub/d.txt"
printf '' > "$temporary_directory/sub/deep/e.txt"

echo "--glob--"
for entry in "$temporary_directory"/*.txt; do
  echo "txt: $(basename "$entry")"
done

echo "--find--"
find "$temporary_directory" -type f -name '*.txt' -printf 'trouve: %f\n' | sort

rm -rf "$temporary_directory"

#!/usr/bin/env bash
# bash → Pattern 06: while read line (line-by-line reading)
export LC_ALL=C

temporary_directory=$(mktemp -d)
input_file="$temporary_directory/input.txt"
printf 'alpha\nbeta gamma\n\ndelta\n' > "$input_file"

line_number=0
while IFS= read -r current_line; do
  line_number=$((line_number + 1))
  echo "$line_number: [$current_line]"
done < "$input_file"
echo "non vides: $(grep -c . "$input_file")"

rm -rf "$temporary_directory"

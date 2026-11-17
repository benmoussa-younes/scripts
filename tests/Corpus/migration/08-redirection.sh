#!/usr/bin/env bash
# bash → Pattern 08: redirections (>, >>) and here-doc
export LC_ALL=C

temporary_directory=$(mktemp -d)
target_file="$temporary_directory/out.txt"
command_output_file="$temporary_directory/captured.txt"
user_name="ana"

printf 'premiere\n' > "$target_file"
printf 'deuxieme\n' >> "$target_file"
cat <<EOF >> "$target_file"
ligne un
  ligne deux indentee
bonjour $user_name
EOF
cat "$target_file"

echo capture > "$command_output_file"
cat "$command_output_file"

echo ignore > /dev/null

rm -rf "$temporary_directory"

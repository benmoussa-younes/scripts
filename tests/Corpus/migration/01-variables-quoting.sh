#!/usr/bin/env bash
# bash → Pattern 01: variables and quotes (values with spaces)
export LC_ALL=C

temporary_directory=$(mktemp -d)
file_name="mon fichier.txt"
greeting="bonjour le monde"
target_file="$temporary_directory/$file_name"

echo "nom=[$file_name]"
echo "salutation: $greeting"
echo 'prix: 5$ {a}'
printf '[%s]\n' "$file_name" x

printf 'contenu\n' > "$target_file"
if [ -f "$target_file" ]; then
  echo "existe: $(basename "$target_file")"
fi
content=$(cat "$target_file")
echo "lu: $content"

rm -rf "$temporary_directory"

#!/usr/bin/env bash
# bash → Pattern 04: file and string tests ([ -f ], [ -d ], [ -s ], -z, -n)
export LC_ALL=C

file_kind() {
  if [ -L "$1" ]; then echo symlink
  elif [ -d "$1" ]; then echo directory
  elif [ -f "$1" ]; then echo file
  elif [ -e "$1" ]; then echo other
  else echo absent
  fi
}

temporary_directory=$(mktemp -d)
a_file="$temporary_directory/a.txt"
a_directory="$temporary_directory/sub"
missing_entry="$temporary_directory/absent"
printf 'x\n' > "$a_file"
mkdir "$a_directory"

echo "a.txt: $(file_kind "$a_file")"
echo "sub: $(file_kind "$a_directory")"
echo "absent: $(file_kind "$missing_entry")"
if [ -e "$a_file" ]; then a_file_exists=true; else a_file_exists=false; fi
echo "a.txt existe: $a_file_exists"
if [ -s "$a_file" ]; then
  echo "a.txt non vide"
fi
empty_value=""
name_value="scripts"
if [ -z "$empty_value" ]; then
  echo "chaine vide"
fi
if [ -n "$name_value" ]; then
  echo "chaine non vide"
fi

rm -rf "$temporary_directory"

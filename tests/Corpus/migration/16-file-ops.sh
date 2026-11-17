#!/usr/bin/env bash
# bash → Pattern 16: mkdir -p, cp, mv, rm (file operations)
export LC_ALL=C

temporary_directory=$(mktemp -d)
work_directory="$temporary_directory/a"
source_file="$work_directory/one.txt"
copy_target="$work_directory/two.txt"
moved_file="$work_directory/moved.txt"

mkdir -p "$work_directory/b/c"
printf 'data\n' > "$source_file"
cp "$source_file" "$copy_target"
echo "copie ok"
if [ -e "$copy_target" ]; then
  echo "copie refusee: already_exists"
else
  cp "$source_file" "$copy_target"
  echo "copie ecrasee"
fi
cp -f "$source_file" "$copy_target"
echo "copie ecrasee explicitement"
mv "$copy_target" "$moved_file"
if [ -e "$copy_target" ]; then copy_target_exists=true; else copy_target_exists=false; fi
echo "ancien chemin existe: $copy_target_exists"
ls -A "$work_directory"
rm -rf "$work_directory/b"
ls -A "$work_directory"
rm "$moved_file"
if rm "$moved_file" 2>/dev/null; then
  echo "deuxieme rm: supprime"
else
  echo "deuxieme rm: not_found"
fi

rm -rf "$temporary_directory"

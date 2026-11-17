#!/usr/bin/env bash
# bash → Pattern 02: command substitution $(...) and stderr capture
export LC_ALL=C

greeting=$(echo "bonjour")
echo "salutation: $greeting"
shout=$(echo "hello" | tr 'a-z' 'A-Z')
echo "majuscules: $shout"
line_count=$(printf 'a\nb\nc\n' | wc -l)
echo "lignes: $line_count"

out=$(cat /nonexistent-scripts-test/file 2>/dev/null)
code=$?
echo "code=$code"
if [ -z "$out" ]; then
  echo "stdout vide: true"
else
  echo "stdout vide: false"
fi
merged_output=$(cat /nonexistent-scripts-test/file 2>&1)
if [[ "$merged_output" == *"No such file or directory"* ]]; then
  echo "stderr fusionne: true"
else
  echo "stderr fusionne: false"
fi

#!/usr/bin/env bash
# bash → Pattern 14: string manipulation (${var%.txt}, ${#var}, cut, tr, sed)
export LC_ALL=C

file_name="rapport.final.txt"
echo "sans suffixe: ${file_name%.txt}"
echo "extension: ${file_name##*.}"
echo "longueur: ${#file_name}"
echo "remplace: ${file_name/final/v2}"
echo "majuscules: ${file_name^^}"

record_line="a:b:c"
echo "champ 2: $(echo "$record_line" | cut -d: -f2)"
echo "tr -d: $(echo "a b c" | tr -d ' ')"
echo "sed g: $(echo "x.y.z" | sed 's/\./-/g')"

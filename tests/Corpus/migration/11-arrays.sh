#!/usr/bin/env bash
# bash → Pattern 11: indexed arrays
export LC_ALL=C

fruits=("pomme" "poire avec espace" "cerise")
fruits+=("kiwi")
echo "nombre: ${#fruits[@]}"
echo "premier: ${fruits[0]}"
echo "dixieme: ${fruits[10]:-aucun}"
for fruit in "${fruits[@]}"; do
  echo "- $fruit"
done
joined_fruits=$(IFS=,; echo "${fruits[*]}")
echo "joint: $joined_fruits"
sorted_fruits=$(printf '%s\n' "${fruits[@]}" | sort | paste -sd'|' -)
echo "tri: $sorted_fruits"
snapshot=("${fruits[@]}")
fruits+=("fig")
echo "snapshot: ${#snapshot[@]} / fruits: ${#fruits[@]}"

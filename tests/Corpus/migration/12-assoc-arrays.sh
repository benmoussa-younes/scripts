#!/usr/bin/env bash
# bash → Pattern 12: associative arrays (declare -A)
export LC_ALL=C

declare -A ages=([ana]=31 [bob]=27)
ages[carl]=40
ages[ana]=32
echo "taille: ${#ages[@]}"
echo "ana: ${ages[ana]}"
echo "zoe: ${ages[zoe]:--1}"
if [[ -v ages[zoe] ]]; then zoe_present=true; else zoe_present=false; fi
echo "zoe presente: $zoe_present"
if [[ -v ages[bob] ]]; then
  unset 'ages[bob]'
  bob_removed=true
else
  bob_removed=false
fi
echo "bob supprime: $bob_removed"
for age_key in $(printf '%s\n' "${!ages[@]}" | sort); do
  echo "$age_key=${ages[$age_key]}"
done

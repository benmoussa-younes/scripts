#!/usr/bin/env bash
# bash → Pattern 19: arithmetic ($((a + b)))
export LC_ALL=C

left_operand=7
right_operand=3
negative_value=-7
echo "somme: $((left_operand + right_operand))"
echo "division: $((left_operand / right_operand))"
echo "reste: $((left_operand % right_operand))"
echo "negatif division: $((negative_value / 2))"
echo "negatif reste: $((negative_value % 3))"
if (( left_operand > right_operand )); then
  echo "7 > 3"
fi

total=0
for step_value in 1 2 3 4; do
  total=$((total + step_value))
done
echo "total: $total"

counter=0
while [ "$counter" -lt 3 ]; do
  counter=$((counter + 1))
done
echo "compteur: $counter"

text_number="42"
echo "analyse: $((text_number + 1))"
bad_text="4x2"
if [[ "$bad_text" =~ ^[+-]?[0-9]+$ ]]; then
  echo "nombre: $bad_text"
else
  echo "erreur: invalid_integer"
fi

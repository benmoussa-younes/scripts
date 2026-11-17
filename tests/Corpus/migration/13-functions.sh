#!/usr/bin/env bash
# bash → Pattern 13: functions (arguments, return, recursion)
export LC_ALL=C

greeting_word="Bonjour"

greet() { echo "$greeting_word $1$2"; }
add_numbers() { echo $(( $1 + $2 )); }
is_even() { [ $(( $1 % 2 )) -eq 0 ]; }
factorial() {
  local value=$1
  if [ "$value" -le 1 ]; then
    echo 1
  else
    echo $(( value * $(factorial $(( value - 1 ))) ))
  fi
}
safe_divide() {
  if [ "$2" -eq 0 ]; then
    DIVIDE_ERROR="division par zero"
    return 1
  fi
  DIVIDE_RESULT=$(( $1 / $2 ))
}

greet ana '!'
greet bob '?'
echo "somme: $(add_numbers 3 4)"
if is_even 4; then echo "pair(4): true"; else echo "pair(4): false"; fi
if is_even 7; then echo "pair(7): true"; else echo "pair(7): false"; fi
echo "factorielle(5): $(factorial 5)"
if safe_divide 10 2; then echo "quotient: $DIVIDE_RESULT"; else echo "erreur: $DIVIDE_ERROR"; fi
if safe_divide 10 0; then echo "quotient: $DIVIDE_RESULT"; else echo "erreur: $DIVIDE_ERROR"; fi

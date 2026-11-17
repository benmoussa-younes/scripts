#!/usr/bin/env bash
# bash → Pattern 18: regular expressions ([[ s =~ re ]])
export LC_ALL=C

digits_pattern='^[0-9]+$'
candidates=("12345" "12a45" "" "007")
for candidate in "${candidates[@]}"; do
  if [[ "$candidate" =~ $digits_pattern ]]; then all_digits=true; else all_digits=false; fi
  echo "[$candidate]: $all_digits"
done

email_pattern='^[a-z]+@[a-z]+\.com$'
emails=("ana@example.com" "ana@example.org" "Ana@example.com")
for email_text in "${emails[@]}"; do
  if [[ "$email_text" =~ $email_pattern ]]; then is_email=true; else is_email=false; fi
  echo "$email_text: $is_email"
done

word_pattern='^[a-z]{2,}$'
if [[ "ab" =~ $word_pattern ]]; then short_word_ok=true; else short_word_ok=false; fi
if [[ "a" =~ $word_pattern ]]; then single_letter_ok=true; else single_letter_ok=false; fi
echo "ab: $short_word_ok"
echo "a: $single_letter_ok"

printf 'x\n' | grep -E '(' > /dev/null 2>&1
if [ "$?" -eq 2 ]; then
  echo "motif invalide: invalid_regex"
else
  echo "motif valide"
fi

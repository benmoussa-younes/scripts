#!/usr/bin/env bash
# bash → Pattern 17: case ... esac
export LC_ALL=C

classify() {
  case "$1" in
    start|begin) echo "demarrage" ;;
    stop) echo "arret" ;;
    *) echo "inconnu" ;;
  esac
}

describe_code() {
  case "$1" in
    0) echo "succes" ;;
    1|2) echo "avertissement" ;;
    *) echo "erreur" ;;
  esac
}

kind_of_file() {
  case "$1" in
    *.txt) echo "texte" ;;
    *.log) echo "journal" ;;
    *) echo "autre" ;;
  esac
}

for word in start begin stop bogus; do
  echo "$word -> $(classify "$word")"
done
for code_value in 0 2 127; do
  echo "$code_value -> $(describe_code "$code_value")"
done
for candidate_name_value in x.txt y.log z.bin; do
  echo "$candidate_name_value -> $(kind_of_file "$candidate_name_value")"
done

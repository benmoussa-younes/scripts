#!/usr/bin/env bash
# bash → Pattern 15: guaranteed cleanup (trap ... EXIT)
export LC_ALL=C

temporary_directory=$(mktemp -d)
cleanup() {
  rm -rf "$temporary_directory"
  echo "nettoyage termine"
}
trap cleanup EXIT

printf 'donnees\n' > "$temporary_directory/work.txt"
echo "travail: $(cat "$temporary_directory/work.txt")"
if ! false; then
  echo "echec: etape en echec"
  exit 0
fi
echo "jamais affiche"

#!/usr/bin/env bash
# bash → Pattern 03: exit status ($?), || and set -e
STEP_ERROR=""

run_step() {
  local code=0
  "$@" || code=$?
  if [ "$code" -ne 0 ]; then
    STEP_ERROR="echec de $1, code $code"
    return 1
  fi
  return 0
}

run_all() {
  run_step true || return 1
  echo "etape A faite"
  run_step false || return 1
  echo "etape B faite"
}

if run_step true; then echo "etape 1 ok"; else echo "etape 1: $STEP_ERROR"; fi
if run_step false; then echo "etape 2 ok"; else echo "etape 2: $STEP_ERROR"; fi
if run_all; then echo "tout est ok"; else echo "arret: $STEP_ERROR"; fi

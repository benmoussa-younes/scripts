#!/usr/bin/env bash
# bash → Pattern 20: script arguments ($1, $@, $#)
export LC_ALL=C

report_arguments() {
  echo "arguments: $#"
  local target_name=${1:-monde}
  echo "Bonjour $target_name"
  local received_argument
  for received_argument in "$@"; do
    echo "arg=[$received_argument]"
  done
}

report_arguments "$@"
report_arguments un "deux mots"

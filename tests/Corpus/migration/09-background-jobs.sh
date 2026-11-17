#!/usr/bin/env bash
# bash → Pattern 09: background jobs (&, wait, $!)
export LC_ALL=C

temporary_directory=$(mktemp -d)
pids=()
for job_number in 1 2 3; do
  printf 'job %s\n' "$job_number" > "$temporary_directory/job$job_number.out" &
  pids+=("$!")
done

job_number=0
for running_pid in "${pids[@]}"; do
  job_number=$((job_number + 1))
  wait "$running_pid"
  finished_code=$?
  echo "$(cat "$temporary_directory/job$job_number.out") termine, code $finished_code"
done

false &
failing_pid=$!
wait "$failing_pid"
failing_code=$?
echo "job en echec: code $failing_code"

rm -rf "$temporary_directory"

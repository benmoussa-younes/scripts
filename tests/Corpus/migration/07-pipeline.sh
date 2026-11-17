#!/usr/bin/env bash
# bash → Pattern 07: pipelines (ps | grep | wc -l, sort -u, xargs)
export LC_ALL=C

nginx_count=$(printf 'nginx master\nsshd main\nnginx worker\n' | grep nginx | wc -l)
echo "processus nginx: $nginx_count"

sorted_unique=$(printf 'b\na\nc\na\n' | sort -u | paste -sd, -)
echo "tri unique: $sorted_unique"

shouted_names=$(printf 'ann\nbob\n' | tr 'a-z' 'A-Z' | paste -sd, -)
echo "majuscules: $shouted_names"

printf 'one\ntwo\n' | xargs -n1 echo item:

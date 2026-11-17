#!/usr/bin/env bash
# bash → Pattern 21: build and emit JSON
export LC_ALL=C

nom="alice"
n=1
printf '{"nom":"%s","n":%d}\n' "$nom" "$n"

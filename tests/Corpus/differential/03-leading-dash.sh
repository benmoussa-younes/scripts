#!/usr/bin/env bash
# bash → a value starting with - is seen as an option as soon as it is
# passed to a program that parses its options.
export LC_ALL=C

value="-n"
# printf interprets "$value" as its first format/option argument.
printf "$value"
echo ""
# echo -n removes the final newline.
echo "$value hello"

#!/usr/bin/env bash
# bash → IFS drives splitting: a value "a:b:c" becomes a list
# as soon as IFS contains ":", without the program having asked for it.
export LC_ALL=C

value="a:b:c"
IFS=:
# Deliberately unquoted: IFS splits the value into three arguments.
printf '[%s]\n' $value

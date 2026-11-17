#!/usr/bin/env bash
# bash → the exit code of the last command lives in $?; it is overwritten
# by any following command, including a simple echo.
export LC_ALL=C

false
status=$?
echo "code direct: $status"
# An intermediate echo overwrites $?:
false
echo "message"
echo "code apres echo: $?"

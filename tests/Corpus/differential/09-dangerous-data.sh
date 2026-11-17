#!/usr/bin/env bash
# bash → data containing ";" reinjected into a command line
# (sh -c, eval) is executed as a command: this is the injection door.
# The payload is made harmless here (printf); with "; rm -rf /"
# instead, the deletion would really be executed.
export LC_ALL=C

payload="; printf 'INJECTE\n' ; true"
# sh -c reinterprets the string: the ";" separates commands.
sh -c "printf '[%s]\n' $payload"

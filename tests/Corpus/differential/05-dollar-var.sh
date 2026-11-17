#!/usr/bin/env bash
# bash → "$" triggers a variable expansion: the text "$5" and "$name"
# are replaced (here by emptiness), the displayed string is no longer the source string.
export LC_ALL=C

name="monde"
echo "msg: cout \$5 pour \$name"
# In double quotes, bash expands: $5 (empty positional parameter) and $name.
echo "msg: cout $5 pour $name"

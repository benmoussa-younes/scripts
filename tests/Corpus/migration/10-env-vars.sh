#!/usr/bin/env bash
# bash → Pattern 10: environment variables (${VAR:-default}, export)
export LC_ALL=C

unset SCRIPTS_DEMO_UNSET
echo "defaut: ${SCRIPTS_DEMO_UNSET:-valeur par defaut}"

export SCRIPTS_DEMO_NAME="craft shell"
echo "dans le fils: $(printenv SCRIPTS_DEMO_NAME)"
unset SCRIPTS_DEMO_NAME
printenv SCRIPTS_DEMO_NAME > /dev/null
echo "apres remove: code $?"
if [ -z "${SCRIPTS_DEMO_NAME+set}" ]; then is_absent=true; else is_absent=false; fi
echo "est absente: $is_absent"

export SCRIPTS_DEMO_EMPTY=""
echo "vide: ${SCRIPTS_DEMO_EMPTY:-valeur par defaut}"
unset SCRIPTS_DEMO_EMPTY

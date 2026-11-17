#!/usr/bin/env bash
# packaging/smoke-test.sh — "smoke test" install test of the scripts package.
#
# Requires NEITHER sudo NOR root NOR chroot: it EXTRACTS the .deb (dpkg-deb -x) into an
# isolated directory and checks the contents of the extracted binary.
#
# Checks:
#   1. the .deb exists and is readable;
#   2. extraction into a temporary directory;
#   3. usr/bin/scripts --version prints the full package version "scripts <version>"
#      (a trailing +… or _… suffix is accepted);
#   4. usr/bin/scripts describe --format json produces valid JSON;
#   5. ldd of the extracted binary lists only libm / libc / ld-linux;
#   6. dpkg-deb --info and --contents show a well-formed package, without a Conflicts field;
#   7. lintian is run if available (warning only if it is absent).
set -euo pipefail

# --- Constants -------------------------------------------------------------
PAQUET="scripts"
ARCHITECTURE="amd64"

RACINE_DEPOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOSSIER_PACKAGING="${RACINE_DEPOT}/packaging"
# We take the newest .deb produced by build-deb.sh (its name carries the version).
DEB="$(ls -t "${DOSSIER_PACKAGING}"/${PAQUET}_*_${ARCHITECTURE}.deb 2>/dev/null | head -1)"
if [ -z "${DEB}" ]; then
    printf 'ERROR: no %s_*_%s.deb package in %s\n(run packaging/build-deb.sh first)\n' \
        "${PAQUET}" "${ARCHITECTURE}" "${DOSSIER_PACKAGING}" >&2
    exit 1
fi
# Full version read from the .deb name; used to check the Version: field and --version.
VERSION="$(basename "${DEB}" | sed -E "s/^${PAQUET}_(.+)_${ARCHITECTURE}\\.deb$/\\1/")"

TMP_BASE="${TMPDIR:-/tmp}"
DOSSIER_TEST="${TMP_BASE}/scripts-deb-smoke.$$"
RACINE_EXTRAITE="${DOSSIER_TEST}/root"

# --- Utilities --------------------------------------------------------------
ok()    { printf '  [OK]   %s\n' "$*"; }
echec() { printf '  [FAIL] %s\n' "$*" >&2; ECHECS=$((ECHECS + 1)); }
info()  { printf '[smoke-test] %s\n' "$*"; }

ECHECS=0

nettoyage() { rm -rf "${DOSSIER_TEST}"; }
trap nettoyage EXIT

# --- Preflight checks -------------------------------------------------------
command -v dpkg-deb >/dev/null 2>&1 || { printf 'ERROR: "dpkg-deb" not found.\n' >&2; exit 1; }
[ -f "${DEB}" ] || { printf 'ERROR: package missing: %s\n(run packaging/build-deb.sh first)\n' "${DEB}" >&2; exit 1; }

info "package: ${DEB}"

# --- 6a. dpkg-deb --info / --contents ---------------------------------------
info "inspecting the package (dpkg-deb --info / --contents)…"
printf '%s\n' '--- dpkg-deb --info ---'
dpkg-deb --info "${DEB}"
printf '%s\n' '--- dpkg-deb --contents ---'
dpkg-deb --contents "${DEB}"

# A missing field or a broken header would make --info fail; we also check the
# required fields (one -f call per field, output without a prefix).
champ() { dpkg-deb -f "${DEB}" "$1"; }
valeur_package="$(champ Package)"
valeur_version="$(champ Version)"
valeur_arch="$(champ Architecture)"
valeur_depends="$(champ Depends)"
valeur_conflicts="$(champ Conflicts)"
printf 'fields: Package=%s Version=%s Architecture=%s Depends=[%s] Conflicts=[%s]\n' \
    "${valeur_package}" "${valeur_version}" "${valeur_arch}" "${valeur_depends}" "${valeur_conflicts}"
[ "${valeur_package}" = "${PAQUET}" ]       || echec "unexpected Package field: \"${valeur_package}\""
[ "${valeur_version}" = "${VERSION}" ]      || echec "unexpected Version field: \"${valeur_version}\""
[ "${valeur_arch}" = "${ARCHITECTURE}" ]    || echec "unexpected Architecture field: \"${valeur_arch}\""
case "${valeur_depends}" in
    *"libc6"*) : ;;
    *) echec "Depends field does not contain libc6: \"${valeur_depends}\"" ;;
esac
# The package declares no Conflicts field.
if [ -n "${valeur_conflicts}" ]; then
    echec "unexpected Conflicts field (the package must no longer conflict): \"${valeur_conflicts}\""
fi
ok "dpkg-deb --info / --contents read the package without error"

# --- 2. Extraction ----------------------------------------------------------
info "extracting the package into an isolated environment…"
mkdir -p "${RACINE_EXTRAITE}"
dpkg-deb -x "${DEB}" "${RACINE_EXTRAITE}"
BINAIRE="${RACINE_EXTRAITE}/usr/bin/scripts"
[ -f "${BINAIRE}" ] || { echec "usr/bin/scripts missing after extraction"; printf 'Result: FAIL\n' >&2; exit 1; }
[ -x "${BINAIRE}" ] || echec "usr/bin/scripts is not executable"
ok "usr/bin/scripts extracted and executable"

# --- 3. --version -----------------------------------------------------------
# The binary reports the FULL version computed from git (e.g.
# `scripts X.Y.Z+N.<hash>` or `scripts X.Y.Z+build.<hash>`); build-deb.sh uses that
# same full version in the `.deb` name and the `Version:` field, and VERSION
# above is read from that name. The check therefore requires the `--version`
# output to equal `scripts <full version>` or to extend that prefix with a `+…` or
# `_…` suffix.
sortie_version="$("${BINAIRE}" --version 2>&1 || true)"
prefixe_version="scripts ${VERSION}"
case "${sortie_version}" in
    "${prefixe_version}"|"${prefixe_version}"+*|"${prefixe_version}"_*)
        ok "\"scripts --version\" => \"${sortie_version}\" (prefix \"${prefixe_version}\" conforms)"
        ;;
    *)
        echec "\"scripts --version\" => \"${sortie_version}\" (expected the prefix \"${prefixe_version}\")"
        ;;
esac

# --- 4. describe --format json ---------------------------------------------
sortie_json="$("${BINAIRE}" describe --format json 2>/dev/null || true)"
premier_caractere="${sortie_json:0:1}"
if [ -n "${sortie_json}" ] && [ "${premier_caractere}" = "{" ]; then
    if command -v python3 >/dev/null 2>&1; then
        if printf '%s' "${sortie_json}" | python3 -c 'import sys,json; json.loads(sys.stdin.read())' 2>/dev/null; then
            ok "\"scripts describe --format json\" produces valid JSON (${#sortie_json} bytes)"
        else
            echec "\"scripts describe --format json\": invalid JSON"
        fi
    else
        ok "\"scripts describe --format json\" starts with \"{\" (python3 absent: JSON validation not performed)"
    fi
else
    echec "\"scripts describe --format json\" does not produce a JSON object"
fi

# --- 5. ldd ----------------------------------------------------------------
if command -v ldd >/dev/null 2>&1; then
    info "dynamic dependencies of the extracted binary (ldd)…"
    sortie_ldd="$(ldd "${BINAIRE}" 2>&1 || true)"
    printf '%s\n' "${sortie_ldd}"
    inattendu=0
    while IFS= read -r ligne; do
        [ -z "${ligne}" ] && continue
        case "${ligne}" in
            *linux-vdso.so*|*libm.so*|*libc.so*|*ld-linux*|*libgcc_s*|*libpthread*|*libdl*|*librt*|*libstdc++*)
                : ;;
            *ldd:*|*"not a dynamic executable"*)
                : ;;
            *) inattendu=1; echec "unexpected dependency: ${ligne}" ;;
        esac
    done <<< "${sortie_ldd}"
    [ "${inattendu}" -eq 0 ] && ok "ldd: only base system libraries (libm/libc/ld-linux)"
else
    info "ldd unavailable: dependency check skipped"
fi

# --- 7. lintian -------------------------------------------------------------
if command -v lintian >/dev/null 2>&1; then
    info "lintian…"
    sortie_lintian="$(lintian --tag-display-limit 0 "${DEB}" 2>&1 || true)"
    printf '%s\n' "${sortie_lintian}"
    if printf '%s\n' "${sortie_lintian}" | grep -q '^E:'; then
        echec "lintian emitted at least one error (\"E:\" line above)"
    elif printf '%s\n' "${sortie_lintian}" | grep -q 'synopsis-too-long'; then
        echec "lintian: the Debian synopsis is too long (keep it under 80 characters)"
    elif printf '%s\n' "${sortie_lintian}" | grep -q '^W:'; then
        ok "lintian: no errors (non-blocking \"W:\" warnings above)"
    else
        ok "lintian: no remarks"
    fi
else
    info "lintian unavailable: static package analysis skipped"
fi

# --- Summary -----------------------------------------------------------------
printf '\n'
if [ "${ECHECS}" -eq 0 ]; then
    printf 'Result: SUCCESS (%s)\n' "$(basename "${DEB}")"
    exit 0
else
    printf 'Result: FAIL — %d check(s) failed\n' "${ECHECS}" >&2
    exit 1
fi

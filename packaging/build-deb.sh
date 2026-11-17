#!/usr/bin/env bash
# packaging/build-deb.sh — builds the Debian package scripts (scripts).
#
# Steps:
#   1. publish the Native AOT binary (dotnet publish -c Release -r linux-x64);
#   2. assemble a package tree (usr/bin/scripts, mode 755);
#   3. generate DEBIAN/control (packaging/debian/control, or built-in fallback);
#   4. build the .deb with dpkg-deb --build --root-owner-group (no dh/debuild);
#   5. normalize timestamps (SOURCE_DATE_EPOCH) for a reproducible build;
#   6. write packaging/SHA256SUMS.
#
# No dependency on Debhelper: a package assembled by hand with dpkg-deb is enough.
set -euo pipefail

# --- Constants -------------------------------------------------------------
PAQUET="scripts"
ARCHITECTURE="amd64"
BINAIRE="scripts"

# Single project version: computed from git (tools/scripts/binaries/version.sh), the same as
# the one compiled into the binary (LanguageReference.Version). The Version
# field of DEBIAN/control is rewritten below with this value. A tag build names the
# package X.Y.Z; a clean post-tag build names it X.Y.Z+N.<hash>, the version the
# binary also reports, so a development package never overwrites a release one.
RACINE_DEPOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION_COMPLETE="$(sh "${RACINE_DEPOT}/tools/scripts/binaries/version.sh")"
VERSION="$(printf '%s' "${VERSION_COMPLETE}" | sed 's/[+_].*$//')"
[ -n "${VERSION}" ] || VERSION="0.1.0"
# The package carries the full computed version (tag: X.Y.Z, post-tag: X.Y.Z+N.<hash>), the one the
# binary reports, so a development package never overwrites a release one.
DEB_VERSION="${VERSION_COMPLETE:-${VERSION}}"
# Reproducible timestamp: identical for all files and archive headers.
SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-1700000000}"

DOSSIER_PACKAGING="${RACINE_DEPOT}/packaging"
CONTROL_SOURCE="${DOSSIER_PACKAGING}/debian/control"

# Temporary working directory (never /tmp directly).
TMP_BASE="${TMPDIR:-/tmp}"
DOSSIER_TRAVAIL="${TMP_BASE}/scripts-deb-build.$$"

# Output of the published binary.
BINAIRE_PUBLIE="${RACINE_DEPOT}/src/Scripts.App/bin/Release/net10.0/linux-x64/publish/${BINAIRE}"

# Name of the produced .deb (where this script lives, next to SHA256SUMS).
DEB_FINAL="${DOSSIER_PACKAGING}/${PAQUET}_${DEB_VERSION}_${ARCHITECTURE}.deb"

# --- Utilities --------------------------------------------------------------
msg()  { printf '%s\n' "$*"; }
info() { printf '[build-deb] %s\n' "$*"; }
warn() { printf '[build-deb] WARNING: %s\n' "$*" >&2; }
die()  { printf '[build-deb] ERROR: %s\n' "$*" >&2; exit 1; }

nettoyage() { rm -rf "${DOSSIER_TRAVAIL}"; }
trap nettoyage EXIT

# --- Preflight checks -------------------------------------------------------
# A dirty tree is refused: packaging uncommitted work under a released name is exactly how a
# released artefact gets overwritten (checked here, where `die` exists).
# A repository with no commit cannot be clean: `version.sh` falls back to 0.1.0 without a
# describe, so the `_dirty` suffix cannot appear; refuse explicitly.
if ! git -C "${RACINE_DEPOT}" rev-parse --verify HEAD >/dev/null 2>&1; then
    die "the repository has no commit: commit the release tree before packaging."
fi
# Untracked source files are compiled by the SDK globs but absent from git: a package
# built with them would not match the tagged source. `--untracked-files=all` also sees
# files inside an untracked directory.
if [ -n "$(git -C "${RACINE_DEPOT}" status --porcelain --untracked-files=all)" ]; then
    die "the tree is dirty or has untracked files: commit first, so the package matches the tagged source."
fi
case "${VERSION_COMPLETE}" in
    *_*) die "the tree is dirty (${VERSION_COMPLETE}): commit before packaging, so the package matches the binary." ;;
esac
# The signed evidence in the output directory belongs to one release. Writing another version's
# SHA256SUMS there would silently invalidate SHA256SUMS.asc: a development build on a clean tree is
# exactly that case, so it is refused. Move the released artefacts away to cut a new release.
if [ -f "${DOSSIER_PACKAGING}/SHA256SUMS.asc" ] && [ -f "${DOSSIER_PACKAGING}/SHA256SUMS" ]; then
    signed_version="$(sed -nE 's/^[0-9a-f]{64}  '"${PAQUET}"'_(.+)_'"${ARCHITECTURE}"'\.deb$/\1/p' \
        "${DOSSIER_PACKAGING}/SHA256SUMS" | head -n 1)"
    if [ -n "${signed_version}" ] && [ "${signed_version}" != "${DEB_VERSION}" ]; then
        die "packaging/ holds the signed ${signed_version} release; refusing to replace its SHA256SUMS with ${DEB_VERSION}. Move the released artefacts out of packaging/ first."
    fi
fi

command -v dotnet    >/dev/null 2>&1 || die "\"dotnet\" was not found in PATH."
command -v dpkg-deb  >/dev/null 2>&1 || die "\"dpkg-deb\" was not found (dpkg package required)."
command -v git       >/dev/null 2>&1 || die "\"git\" was not found in PATH (needed to prove the tree and its version)."

case "${SOURCE_DATE_EPOCH}" in
    ''|*[!0-9]*) die "SOURCE_DATE_EPOCH must be an integer of Unix seconds (received: \"${SOURCE_DATE_EPOCH}\")." ;;
esac

[ -f "${CONTROL_SOURCE}" ] || die "missing metadata: ${CONTROL_SOURCE}"

# The shipped changelog must describe the package that carries it: a package with a different
# top entry misleads `apt changelog` (release audit m6).
CHANGELOG_SOURCE="${DOSSIER_PACKAGING}/debian/changelog"
if [ -f "${CHANGELOG_SOURCE}" ]; then
    CHANGELOG_VERSION="$(sed -n '1s/^[^(]*(\([^)]*\)).*/\1/p' "${CHANGELOG_SOURCE}")"
    [ "${CHANGELOG_VERSION}" = "${VERSION}" ] \
        || die "debian/changelog starts at ${CHANGELOG_VERSION}, the tree is ${VERSION}: add the release entry first."
fi

info "version=${DEB_VERSION} (base ${VERSION}) architecture=${ARCHITECTURE} SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH}"

# --- 1. Publish the AOT binary ---------------------------------------------
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
info "publishing the AOT binary (dotnet publish -c Release -r linux-x64)…"
if ! dotnet publish "${RACINE_DEPOT}/src/Scripts.App/Scripts.App.csproj" -c Release -r linux-x64; then
    die "the AOT publish failed (Native AOT unavailable or compilation error)."
fi
[ -x "${BINAIRE_PUBLIE}" ] || die "the published binary is missing or not executable: ${BINAIRE_PUBLIE}"

# --- 2. Assemble the package tree -------------------------------------------
info "assembling the package tree…"
rm -rf "${DOSSIER_TRAVAIL}"
mkdir -p "${DOSSIER_TRAVAIL}/racine/usr/bin"
mkdir -p "${DOSSIER_TRAVAIL}/racine/DEBIAN"
mkdir -p "${DOSSIER_TRAVAIL}/racine/usr/share/doc/${PAQUET}"

install -m 0755 "${BINAIRE_PUBLIE}" "${DOSSIER_TRAVAIL}/racine/usr/bin/${BINAIRE}"

# Debian documents (copyright + compressed changelog).
if [ -f "${DOSSIER_PACKAGING}/debian/copyright" ]; then
    install -m 0644 "${DOSSIER_PACKAGING}/debian/copyright" \
        "${DOSSIER_TRAVAIL}/racine/usr/share/doc/${PAQUET}/copyright"
fi
# Licence texts (GPL v3 for scripts, LGPL v3 for the vendored Core subset), shipped verbatim,
# plus NOTICE and THIRD-PARTY, which record the provenance of the vendored source.
for document in "${RACINE_DEPOT}/LICENSE" "${RACINE_DEPOT}/LICENSE.LGPL3" "${RACINE_DEPOT}/NOTICE" "${RACINE_DEPOT}/THIRD-PARTY"; do
    [ -f "${document}" ] || continue
    install -m 0644 "${document}" "${DOSSIER_TRAVAIL}/racine/usr/share/doc/${PAQUET}/$(basename "${document}")"
done
install -m 0644 "${RACINE_DEPOT}/LICENSES/CC-BY-SA-4.0.txt" \
    "${DOSSIER_TRAVAIL}/racine/usr/share/doc/${PAQUET}/LICENSE.CC-BY-SA-4.0"
if [ -f "${DOSSIER_PACKAGING}/debian/changelog" ]; then
    # gzip -n: records neither name nor timestamp in the header (reproducibility).
    gzip -n -9 -c "${DOSSIER_PACKAGING}/debian/changelog" \
        > "${DOSSIER_TRAVAIL}/racine/usr/share/doc/${PAQUET}/changelog.gz"
    chmod 0644 "${DOSSIER_TRAVAIL}/racine/usr/share/doc/${PAQUET}/changelog.gz"
fi

# md5sums: lets `dpkg -V` and `debsums` verify the installed files.
(
    cd "${DOSSIER_TRAVAIL}/racine"
    find . -type f ! -path "./DEBIAN/*" -printf '%P\n' | LC_ALL=C sort \
        | xargs -r md5sum > DEBIAN/md5sums
    chmod 0644 DEBIAN/md5sums
)

# --- 3. Generate the DEBIAN/control metadata --------------------------------
info "generating DEBIAN/control…"
# We start from the versioned file; we force the fields versioned by the script
# in case they diverge, then we guarantee a final newline.
{
    awk '
        BEGIN { vu_version=0; vu_arch=0 }
        /^Version:/       { print "Version: " DEB_VERSION;  vu_version=1; next }
        /^Architecture:/  { print "Architecture: " ARCH;    vu_arch=1;    next }
        { print }
        END {
            if (!vu_version) print "Version: " DEB_VERSION
            if (!vu_arch)    print "Architecture: " ARCH
        }
    ' DEB_VERSION="${DEB_VERSION}" ARCH="${ARCHITECTURE}" "${CONTROL_SOURCE}"
} > "${DOSSIER_TRAVAIL}/racine/DEBIAN/control"

# Installed-Size = sum of 1 KiB blocks rounded up (like dh_installdeb).
TAILLE_KIO=$(( ( $(du -sk "${DOSSIER_TRAVAIL}/racine/usr" | cut -f1) ) ))
{
    awk -v taille="${TAILLE_KIO}" '
        BEGIN { remplace=0 }
        /^Installed-Size:/ { print "Installed-Size: " taille; remplace=1; next }
        { print }
        END { if (!remplace) print "Installed-Size: " taille }
    ' "${DOSSIER_TRAVAIL}/racine/DEBIAN/control" > "${DOSSIER_TRAVAIL}/control.tmp"
}
mv "${DOSSIER_TRAVAIL}/control.tmp" "${DOSSIER_TRAVAIL}/racine/DEBIAN/control"
printf '\n' >> "${DOSSIER_TRAVAIL}/racine/DEBIAN/control"

chmod 0755 "${DOSSIER_TRAVAIL}/racine/DEBIAN"
chmod 0755 "${DOSSIER_TRAVAIL}/racine" "${DOSSIER_TRAVAIL}/racine/usr" "${DOSSIER_TRAVAIL}/racine/usr/bin"
if [ -d "${DOSSIER_TRAVAIL}/racine/usr/share" ]; then
    find "${DOSSIER_TRAVAIL}/racine/usr/share" -type d -exec chmod 0755 {} +
fi

# --- 4. Normalize timestamps (reproducible build) ---------------------------
info "normalizing timestamps to @${SOURCE_DATE_EPOCH}…"
find "${DOSSIER_TRAVAIL}/racine" -exec touch -h -d "@${SOURCE_DATE_EPOCH}" {} + 2>/dev/null \
    || find "${DOSSIER_TRAVAIL}/racine" -exec touch -h -t "$(date -u -d "@${SOURCE_DATE_EPOCH}" +%Y%m%d%H%M.%S)" {} +

# --- 5. Build the .deb ------------------------------------------------------
info "building the .deb (dpkg-deb --build --root-owner-group)…"
DEB_TEMP="${DOSSIER_TRAVAIL}/${PAQUET}_${DEB_VERSION}_${ARCHITECTURE}.deb"
dpkg-deb --build --root-owner-group "${DOSSIER_TRAVAIL}/racine" "${DEB_TEMP}" >/dev/null

# dpkg-deb 1.22.x does not honor SOURCE_DATE_EPOCH for the timestamp of the "ar"
# archive headers (mtime fields of the members debian-binary, control.tar.*,
# data.tar.*): they take the current time, which prevents a bit-for-bit
# identical build. We rewrite these fields to @SOURCE_DATE_EPOCH.
reproductible_ok=0
if command -v python3 >/dev/null 2>&1; then
    if python3 - "${SOURCE_DATE_EPOCH}" "${DEB_TEMP}" <<'PYEOF'
import sys
epoch = int(sys.argv[1])
chemin = sys.argv[2]
with open(chemin, "rb") as flux:
    donnees = bytearray(flux.read())
if donnees[:8] != b"!<arch>\n":
    sys.exit(3)
offset = 8
n = 0
while offset + 60 <= len(donnees):
    entete = donnees[offset:offset + 60]
    if entete[58:60] != b"\x60\n":
        break
    donnees[offset + 16:offset + 28] = (str(epoch).ljust(12))[:12].encode("ascii")
    n += 1
    taille = int(entete[48:58].decode("ascii").strip())
    offset += 60 + taille + (taille & 1)
with open(chemin, "wb") as flux:
    flux.write(donnees)
PYEOF
    then
        reproductible_ok=1
    fi
fi

if [ "${reproductible_ok}" -eq 0 ]; then
    warn "\"ar\" header timestamps not normalized: this .deb is not guaranteed"
    warn "bit-for-bit identical between two builds (dpkg-deb does not honor SOURCE_DATE_EPOCH"
    warn "and \"python3\" is unavailable to rewrite the archive headers)."
    warn "The package is published and the SHA256 below remains valid for THIS file."
fi

# --- 6. Output and checksum -------------------------------------------------
mv "${DEB_TEMP}" "${DEB_FINAL}"

(
    cd "${DOSSIER_PACKAGING}"
    sha256sum "${PAQUET}_${DEB_VERSION}_${ARCHITECTURE}.deb" > SHA256SUMS
)

taille_octets="$(stat -c '%s' "${DEB_FINAL}")"
somme="$(cut -d' ' -f1 "${DOSSIER_PACKAGING}/SHA256SUMS")"

info "package: ${DEB_FINAL}"
info "size: ${taille_octets} bytes"
info "sha256: ${somme}"
if [ "${reproductible_ok}" -eq 1 ]; then
    info "reproducible build: timestamps normalized (SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH})"
fi
msg ""
msg "Package built: ${DEB_FINAL}"

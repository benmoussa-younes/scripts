#!/usr/bin/env bash
# packaging/build-tarballs.sh — release tarballs of scripts: the binary and the sources.
#
# Produces, in this directory:
#   scripts_<X.Y.Z>_linux-x64.tar.gz   the published Native AOT binary and its licences
#   scripts_<X.Y.Z>_src.tar.gz         the complete corresponding source (GPL §6)
#   tarballs.sha256                              sha256 of both archives
#
# The source archive is the GPL obligation: a binary distributed under GPL-3.0-or-later must come with
# the complete corresponding source. It is `git archive` of the committed tree, minus what
# `.gitattributes` marks `export-ignore` (agent instructions, VCS/editor dotfiles, the
# checksum files), so the repository must be committed and clean (the script refuses a dirty tree: a
# tarball of half-applied work would not correspond to the binary). The binary is the output of
# `packaging/build-deb.sh` (which publishes the AOT binary), so run that first, or let this script run
# it.
#
# Everything is reproducible: a fixed SOURCE_DATE_EPOCH, a stable file order and `--owner=0 --group=0`
# keep the archives byte-identical across runs on the same commit.
#
# Usage: build-tarballs.sh [--skip-deb]
set -euo pipefail

script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository="$(cd "${script_directory}/.." && pwd)"
project_root="${repository}"

command -v git     >/dev/null 2>&1 || { echo "[tarballs] git was not found in PATH" >&2; exit 2; }
command -v python3 >/dev/null 2>&1 || { echo "[tarballs] python3 was not found in PATH (archive normalization)" >&2; exit 2; }

version_full="$(sh "${repository}/tools/scripts/binaries/version.sh")"
version="$(printf '%s' "${version_full}" | sed 's/[+_].*$//')"
[ -n "${version}" ] || version="0.1.0"
# The archive names carry the full version: `X.Y.Z` on a tag, `X.Y.Z+N.<hash>` after it.
# Labelling an archive `X.Y.Z` while HEAD is N commits later claims to be the tag's
# source, which is exactly the 2026-10-24 mistake this script now prevents (P1).
release_version="${version_full}"
[ -n "${release_version}" ] || release_version="${version}"

binary="${repository}/src/Scripts.App/bin/Release/net10.0/linux-x64/publish/scripts"
binary_archive="scripts_${release_version}_linux-x64.tar.gz"
source_archive="scripts_${release_version}_src.tar.gz"
source_prefix="scripts-${release_version}"
checksums="tarballs.sha256"
stamp="${SOURCE_DATE_EPOCH:-1700000000}"

if [ "${1:-}" != "--skip-deb" ] && [ ! -x "${binary}" ]; then
    echo "[tarballs] publishing the AOT binary (build-deb.sh)"
    bash "${script_directory}/build-deb.sh"
fi
[ -x "${binary}" ] || {
    echo "[tarballs] binary not found: ${binary}" >&2
    exit 2
}

# The payload must be the build of this tree: a stale or foreign executable would be labelled
# with HEAD's version (release audit M2).
reported_version="$("${binary}" --version 2>&1 || true)"
if [ "${reported_version}" != "scripts ${release_version}" ]; then
    echo "[tarballs] the binary reports « ${reported_version} », expected « scripts ${release_version} »: rebuild it (build-deb.sh) before archiving" >&2
    exit 5
fi

# The source archive must correspond to the binary: a modified or untracked file would not.
# Untracked SDK-style .cs files are compiled (default globs) but absent from `git archive`
# (release audit M3), so they count as dirty too.
# The pathspecs are relative to ${project_root} (the `git -C` directory), so they must name
# `packaging/...` is relative to the repository root, which is also the `git -C` directory.
packaging_relative="${script_directory#"${project_root}/"}"
if [ -n "$(git -C "${project_root}" status --porcelain --untracked-files=all -- . ":!${packaging_relative}/SHA256SUMS" ":!${packaging_relative}/tarballs.sha256" ":!${packaging_relative}/build-tarballs.sh")" ]; then
    echo "[tarballs] refusing to archive a dirty tree: commit first (the source must match the binary)" >&2
    git -C "${project_root}" status --short --untracked-files=all -- . ":!${packaging_relative}/SHA256SUMS" ":!${packaging_relative}/tarballs.sha256" ":!${packaging_relative}/build-tarballs.sh" >&2
    exit 3
fi

# The source archive is the complete corresponding source of the binary: the
# vendored Core subset is part of it, so no external CORE-COMMIT.txt is needed.

work="$(mktemp -d "${TMPDIR:-/tmp}/scripts-tarballs.XXXXXX")"
trap 'rm -rf "${work}"' EXIT

# --- binary archive -------------------------------------------------------------------------
staging="${work}/${source_prefix}"
mkdir -p "${staging}/licences"
cp "${binary}" "${staging}/scripts"
chmod 755 "${staging}/scripts"
for document in LICENSE LICENSE.LGPL3 NOTICE THIRD-PARTY LICENSES/CC-BY-SA-4.0.txt; do
    [ -f "${project_root}/${document}" ] && cp "${project_root}/${document}" "${staging}/licences/$(basename "${document}")"
done
cat >"${staging}/INSTALL.txt" <<EOF
scripts ${release_version} — Scripts (single native binary, no .NET runtime)

  install -m 755 scripts /usr/local/bin/scripts
  scripts --version

Requires Linux x86_64, glibc >= 2.34; links only against libc and libm.
Licences: GPL-3.0-or-later (scripts), LGPL-3.0-or-later (the vendored Core subset).
The complete corresponding source is in scripts_${release_version}_src.tar.gz.
EOF
tar --sort=name --owner=0 --group=0 --numeric-owner --mtime="@${stamp}" \
    -C "${work}" -czf "${script_directory}/${binary_archive}" "${source_prefix}"

# --- source archive (GPL §6) ----------------------------------------------------------------
git -C "${project_root}" archive --format=tar.gz --prefix="${source_prefix}/" HEAD \
    -o "${script_directory}/${source_archive}"
# Normalize the archive members (owner, group, timestamps) so two runs give the same bytes.
python3 - "${script_directory}/${source_archive}" "${stamp}" "${source_prefix}" "${release_version}" <<'PYNORMALIZE'
import gzip, io, os, sys, tarfile, time
path, stamp = sys.argv[1], int(sys.argv[2])
source_prefix = sys.argv[3] if len(sys.argv) > 3 else ""
release_version = sys.argv[4] if len(sys.argv) > 4 else ""
with tarfile.open(path, "r:gz") as source:
    members = [(member, source.extractfile(member).read() if member.isfile() else None) for member in source.getmembers()]
if release_version and source_prefix:
    # Without git, version.sh reads this file: a build from the source archive reports the
    # release version instead of a hard-coded fallback (release audit M10).
    data = (release_version + "\n").encode()
    info = tarfile.TarInfo(f"{source_prefix}/VERSION")
    info.size = len(data)
    info.mode = 0o644
    info.uid = info.gid = 0
    info.uname = info.gname = "root"
    info.mtime = stamp
    members.append((info, data))
buffer = io.BytesIO()
with tarfile.open(fileobj=buffer, mode="w", format=tarfile.GNU_FORMAT) as target:
    for member, data in members:
        member.uid = member.gid = 0
        member.uname = member.gname = "root"
        member.mtime = stamp
        if data is not None:
            member.size = len(data)
            target.addfile(member, io.BytesIO(data))
        else:
            target.addfile(member)
with open(path, "wb") as raw:
    with gzip.GzipFile(fileobj=raw, mode="wb", mtime=stamp) as compressed:
        compressed.write(buffer.getvalue())
PYNORMALIZE

# --- checksums -------------------------------------------------------------------------------
(
    cd "${script_directory}"
    sha256sum "${binary_archive}" "${source_archive}" >"${checksums}"
)

echo "[tarballs] ${binary_archive} ($(stat -c %s "${script_directory}/${binary_archive}") bytes)"
echo "[tarballs] ${source_archive} ($(stat -c %s "${script_directory}/${source_archive}") bytes)"
echo "[tarballs] ${checksums} written"

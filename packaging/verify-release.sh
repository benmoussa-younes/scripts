#!/usr/bin/env bash
# packaging/verify-release.sh — re-derive the release evidence from the artefacts on disk.
#
# Usage: verify-release.sh [--write] [--require-tag] [--directory DIR]
#   --write        rewrite RELEASE-EVIDENCE from the artefacts (the file is generated,
#                  never hand-edited; keep it with the artefacts it describes)
#   --require-tag  fail unless the artefacts were built from the tagged commit, with no
#                  build metadata in the binary's version (the release gate)
#   --directory    directory holding the artefacts and RELEASE-EVIDENCE (default: this one);
#                  use it to verify a downloaded release without a repository
#
# What is checked:
#   1. SHA256SUMS and tarballs.sha256 match the files on disk;
#   2. the .deb payload and the binary tarball carry the same scripts binary;
#   3. that binary runs and reports the version recorded in the evidence;
#   4. the source archive matches the WHOLE tree of the commit the binary was built from
#      (every tracked file; the injected VERSION is removed before the comparison);
#   5. without --write, every field of RELEASE-EVIDENCE matches the recomputed value.
#
# What it cannot prove: a forged binary that prints the release version passes
# --require-tag. The gate proves coherence (tree, names, checksums) and that the
# artefacts name a tag signed by the maintainer key; it says nothing about which
# machine built them. Build provenance comes from the attestation the release
# workflow attaches (gh attestation verify); the workflow publishes no detached
# OpenPGP signature.
#
# --require-tag verifies the tag signature with gpg: import the maintainer public
# key first, `gpg --import packaging/maintainer-key.asc`.
#
# Exit codes: 0 OK; 2 usage; 3 missing artefact; 4 not runnable or checksum mismatch;
# 5 source mismatch; 6 evidence mismatch; 7 --require-tag failed.
set -euo pipefail
export LC_ALL=C

script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository="$(cd "${script_directory}/.." && pwd)"
project_root="${repository}"
release_directory="${script_directory}"
evidence="${release_directory}/RELEASE-EVIDENCE"

write=0
require_tag=0
while [ "$#" -gt 0 ]; do
    argument="$1"
    shift
    case "${argument}" in
        --write) write=1 ;;
        --require-tag) require_tag=1 ;;
        --directory)
            [ "$#" -gt 0 ] || { echo "usage: verify-release.sh [--write] [--require-tag] [--directory DIR]" >&2; exit 2; }
            release_directory="$(cd "$1" && pwd)"
            evidence="${release_directory}/RELEASE-EVIDENCE"
            shift
            ;;
        *)
            echo "usage: verify-release.sh [--write] [--require-tag] [--directory DIR]" >&2
            exit 2
            ;;
    esac
done

die() {
    local message="$1"
    local status="${2:-1}"
    echo "verify-release: ${message}" >&2
    exit "${status}"
}

[ -f "${release_directory}/SHA256SUMS" ] || die "missing ${release_directory}/SHA256SUMS" 3
[ -f "${release_directory}/tarballs.sha256" ] || die "missing ${release_directory}/tarballs.sha256" 3

deb_name="$(awk 'NR==1 {print $2}' "${release_directory}/SHA256SUMS")"
binary_name="$(awk '/_linux-x64\.tar\.gz$/ {print $2}' "${release_directory}/tarballs.sha256")"
source_name="$(awk '/_src\.tar\.gz$/ {print $2}' "${release_directory}/tarballs.sha256")"
[ -n "${deb_name}" ] || die "SHA256SUMS names no package" 3
[ -n "${binary_name}" ] || die "tarballs.sha256 names no binary archive" 3
[ -n "${source_name}" ] || die "tarballs.sha256 names no source archive" 3

deb="${release_directory}/${deb_name}"
binary_tarball="${release_directory}/${binary_name}"
source_tarball="${release_directory}/${source_name}"
for artefact in "${deb}" "${binary_tarball}" "${source_tarball}"; do
    [ -f "${artefact}" ] || die "missing artefact: ${artefact}" 3
done

# 1. The checksum files must describe the files present.
( cd "${release_directory}" && sha256sum -c SHA256SUMS >/dev/null ) \
    || die "the package checksum does not match SHA256SUMS" 4
( cd "${release_directory}" && sha256sum -c tarballs.sha256 >/dev/null ) \
    || die "a tarball checksum does not match tarballs.sha256" 4

work="$(mktemp -d "${TMPDIR:-/tmp}/scripts-release.XXXXXX")"
trap 'rm -rf "${work}"' EXIT

# 2. Extract the two copies of the deliverable binary and the source markers.
dpkg-deb --fsys-tarfile "${deb}" | tar -xO ./usr/bin/scripts >"${work}/scripts-deb"
binary_member="$(tar -tzf "${binary_tarball}" | grep -m1 '/scripts$' || true)"
[ -n "${binary_member}" ] || die "the binary tarball carries no scripts" 4
tar -xzOf "${binary_tarball}" "${binary_member}" >"${work}/scripts-tar"
chmod 755 "${work}/scripts-deb" "${work}/scripts-tar"

# 3. The two payloads are the same binary, and it runs.
sha256() { sha256sum "$1" | cut -d' ' -f1; }
deb_sha="$(sha256 "${deb}")"
deb_binary="$(sha256 "${work}/scripts-deb")"
binary_tarball_sha="$(sha256 "${binary_tarball}")"
binary_tarball_binary="$(sha256 "${work}/scripts-tar")"
source_tarball_sha="$(sha256 "${source_tarball}")"

[ "${deb_binary}" = "${binary_tarball_binary}" ] \
    || die "the .deb and the binary tarball carry different scripts binaries" 4

version_output="$("${work}/scripts-deb" --version 2>&1)" || die "the packaged scripts does not run: ${version_output}" 4
tar_version_output="$("${work}/scripts-tar" --version 2>&1)" || die "the tarball scripts does not run" 4
[ "${version_output}" = "${tar_version_output}" ] \
    || die "the two scripts binaries report different versions: « ${version_output} » / « ${tar_version_output} »" 4
version="${version_output#scripts }"
case "${version}" in
    "${version_output}") die "unexpected --version output: ${version_output}" 4 ;;
esac

version_base="$(printf '%s' "${deb_name}" | sed -E 's/^scripts_([^_]+)_amd64\.deb$/\1/')"
case "${version}" in
    "${version_base}"|"${version_base}"+*|"${version_base}"_*) ;;
    *) die "the package is named ${version_base} but the binary reports ${version}" 4 ;;
esac

# The archive names must carry the version the payload reports (release audit M1). Without
# --require-tag a mismatch is a warning so historical artefacts can still be checked;
# the release gate below makes it fatal.
if [ "${binary_name}" != "scripts_${version}_linux-x64.tar.gz" ]; then
    echo "verify-release: warning: the binary tarball is named ${binary_name}, the payload reports ${version}" >&2
fi
if [ "${source_name}" != "scripts_${version}_src.tar.gz" ]; then
    echo "verify-release: warning: the source tarball is named ${source_name}, the payload reports ${version}" >&2
fi

# 4. Recover the commit the binary was built from: `X.Y.Z+N.<hash>`, `X.Y.Z+build.<hash>`,
#    or a tag whose version is exactly `X.Y.Z`.
built_hash=""
case "${version}" in
    *+build.*) built_hash="${version#*+build.}" ;;
    *+[0-9]*.*) built_hash="${version#*+}"; built_hash="${built_hash#*.}" ;;
esac
built_from=""
if [ -n "${built_hash}" ] && git -C "${project_root}" cat-file -e "${built_hash}^{commit}" 2>/dev/null; then
    built_from="$(git -C "${project_root}" rev-parse --short "${built_hash}^{commit}")"
fi
tag=""
if [ -n "${built_from}" ]; then
    tag="$(git -C "${project_root}" describe --tags --exact-match "${built_from}" 2>/dev/null || true)"
else
    if git -C "${project_root}" rev-parse -q --verify "refs/tags/v${version}^{commit}" >/dev/null; then
        built_from="$(git -C "${project_root}" rev-parse --short "refs/tags/v${version}^{commit}")"
        tag="v${version}"
    fi
fi

if [ -n "${built_from}" ]; then
    # Compare the WHOLE source tree, not one file: a tarball with one modified vendored file
    # would otherwise pass.
    mkdir -p "${work}/source-archive" "${work}/source-tag"
    tar -xzf "${source_tarball}" -C "${work}/source-archive" --strip-components=1
    git -C "${project_root}" archive "${built_from}" | tar -x -C "${work}/source-tag"
    # These two are generated into the archive by build-tarballs.sh, not tracked.
    rm -f "${work}/source-archive/VERSION"
    if ! diff -r "${work}/source-tag" "${work}/source-archive" >/dev/null; then
        die "the source archive does not match the whole tree of ${built_from}" 5
    fi
fi

block="$(printf '%s\t%s\n' \
    version "${version}" \
    tag "${tag}" \
    built_from "${built_from}" \
    deb "${deb_sha}" \
    deb_binary "${deb_binary}" \
    binary_tarball "${binary_tarball_sha}" \
    binary_tarball_binary "${binary_tarball_binary}" \
    source_tarball "${source_tarball_sha}")"

# 5. --require-tag: the release gate.
if [ "${require_tag}" -eq 1 ]; then
    [ -n "${tag}" ] || die "--require-tag: the artefacts were not built from a tag (version ${version})" 7
    case "${version}" in
        *+*|*_*) die "--require-tag: the version carries build metadata: ${version}" 7 ;;
    esac
    tagged_commit="$(git -C "${project_root}" rev-parse --short "refs/tags/${tag}^{commit}")"
    [ "${built_from}" = "${tagged_commit}" ] \
        || die "--require-tag: built from ${built_from}, tag ${tag} is ${tagged_commit}" 7
    # A coherent release is still not an authored release: a forged binary that prints the version
    # passes every check above. The tag gate therefore requires the signed tag — the commit is the
    # maintainer's, and the release workflow attests the bytes it built from that commit.
    command -v gpg >/dev/null 2>&1 \
        || die "--require-tag: gpg is required to verify the signed tag" 7
    git -C "${project_root}" verify-tag "${tag}" >/dev/null 2>&1 \
        || die "--require-tag: ${tag} is not a verified signed tag (import packaging/maintainer-key.asc)" 7
    [ "${binary_name}" = "scripts_${version}_linux-x64.tar.gz" ] \
        || die "--require-tag: the binary tarball name does not carry ${version}" 7
    [ "${source_name}" = "scripts_${version}_src.tar.gz" ] \
        || die "--require-tag: the source tarball name does not carry ${version}" 7
fi

# 6. Write or compare the evidence.
if [ "${write}" -eq 1 ]; then
    printf '%s\n' "${block}" >"${evidence}"
else
    [ -f "${evidence}" ] || die "missing ${evidence}; run verify-release.sh --write" 6
    if ! diff -u "${evidence}" <(printf '%s\n' "${block}"); then
        die "the artefacts do not match RELEASE-EVIDENCE (regenerate with --write after a rebuild)" 6
    fi
fi

echo "verify-release: OK"
echo "  version      ${version}"
echo "  tag          ${tag:-(none)}"
echo "  built from   ${built_from:-unknown}"
echo "  deb          ${deb_sha}"
echo "  scripts binary   ${deb_binary}"

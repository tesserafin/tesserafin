#!/usr/bin/env bash
# The Tesserafin win-x64 FFmpeg runtime build (W1-A3 / #236).
#
# Runs INSIDE an MSYS2 CLANG64 shell whose /clang64 prefix holds exactly the
# 246-package set W1-R retained and installed with `pacman -U` from local files.
# This script never installs a package, never contacts a mirror and never
# downloads a compiled artifact: the toolchain is an INPUT, which is what makes
# bit-for-bit reproducibility reachable at all.
#
# Usage: ci/windows/ffmpeg/build-win-x64.sh --cache DIR --out DIR
#
# Determinism rules, identical in intent to the Linux runtime:
#   * SOURCE_DATE_EPOCH from the pinned FFmpeg baseline, never the clock;
#   * -ffile-prefix-map so no build path is embedded;
#   * -Wl,--no-insert-timestamp, because a PE header carries a build time where
#     an ELF carries a build-id, and two runners will never share a clock;
#   * a FIXED job count, because parallelism must not be an input;
#   * LC_ALL=C and TZ=UTC so no locale or zone leaks into generated files;
#   * fixed absolute paths, never mktemp.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# One definition of "reproducible" for both operating systems: the epoch, the
# manifest reader and the deterministic-tar helper are the Linux runtime's.
# shellcheck source=ci/ffmpeg/lib.sh
source "${HERE}/../../ffmpeg/lib.sh"

WIN_FLAGS_FILE="${WIN_FLAGS_FILE:-${HERE}/ffmpeg-configure.win-x64.txt}"
ARCH="win-x64"

CACHE=""; OUT=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --cache) CACHE="$2"; shift 2 ;;
        --out)   OUT="$2"; shift 2 ;;
        *) ff_die "unknown argument: $1" ;;
    esac
done
[[ -d "${CACHE}" ]] || ff_die "--cache must be an existing source cache"
[[ -n "${OUT}" ]]   || ff_die "--out is required"
[[ -f "${WIN_FLAGS_FILE}" ]] || ff_die "missing ${WIN_FLAGS_FILE}"

# ── the environment must be the one W1-R installed ───────────────────────────
[[ "${MSYSTEM:-}" == "CLANG64" ]] \
    || ff_die "MSYSTEM is '${MSYSTEM:-unset}'; this build is only accepted from an MSYS2 CLANG64 shell"
[[ "$(uname -m)" == "x86_64" ]] \
    || ff_die "this host is $(uname -m); win-x64 must be built natively on x86_64"
case "$(uname -s)" in
    CLANG64*|MINGW64*|MSYS*) : ;;
    *) ff_die "uname -s is '$(uname -s)'; this is not a native Windows MSYS2 shell. Wine and cross-builds are not accepted evidence" ;;
esac

ff_load_manifest

# Python on Windows translates '\n' to '\r\n' on text stdout, and `$(...)` strips
# trailing NEWLINES but not the carriage return in front of them. So every value
# ff_load_manifest read through a `python3 -c` command substitution arrives here
# as "7.1.4-tesserafin.1<CR>" — which then becomes a staging directory with a
# carriage return in its name, and a runtime archive nobody can find.
#
# Stripped here rather than in ci/ffmpeg/lib.sh: that file belongs to the Linux
# runtimes, where the behaviour does not exist, and this work must leave it
# byte-identical.
ff_strip_cr() { printf '%s' "${1//$'\r'/}"; }
FF_BUILD_REVISION="$(ff_strip_cr "${FF_BUILD_REVISION}")"
FF_FFMPEG_COMMIT="$(ff_strip_cr "${FF_FFMPEG_COMMIT}")"
FF_FFMPEG_REPO="$(ff_strip_cr "${FF_FFMPEG_REPO}")"
FF_FFMPEG_BASELINE="$(ff_strip_cr "${FF_FFMPEG_BASELINE}")"
export FF_BUILD_REVISION FF_FFMPEG_COMMIT FF_FFMPEG_REPO FF_FFMPEG_BASELINE
for v in "${FF_BUILD_REVISION}" "${FF_FFMPEG_COMMIT}" "${FF_FFMPEG_BASELINE}"; do
    [[ "${v}" == *$'\r'* ]] && ff_die "a manifest value still carries a carriage return: ${v@Q}"
done
[[ "${FF_FFMPEG_COMMIT}" =~ ^[0-9a-f]{40}$ ]] \
    || ff_die "the FFmpeg commit read from the manifest is not a 40-character SHA: ${FF_FFMPEG_COMMIT@Q}"

SOURCE_DATE_EPOCH="$(ff_source_date_epoch)"
export SOURCE_DATE_EPOCH
export LC_ALL=C LANG=C TZ=UTC

# See the prefix comment in ffmpeg-configure.win-x64.txt: this path is a drive
# root in both its POSIX and its Windows form, so nothing that bakes it in leaks
# a workspace, a runner or an MSYS2 installation.
PREFIX="$(grep -oE '^--prefix=.*' "${WIN_FLAGS_FILE}" | head -1 | cut -d= -f2-)"
[[ "${PREFIX}" == /c/* ]] || ff_die "the win-x64 prefix must be a drive-root path, got '${PREFIX}'"
export PREFIX
BUILDROOT=/c/tf-ffbuild
WORK=/c/tf-ffinstall

if [[ "${FF_INCREMENTAL:-0}" == "1" ]]; then
    ff_log "INCREMENTAL build: completed components are reused. Not valid for reproducibility evidence."
else
    rm -rf "${BUILDROOT}" "${WORK}" "${PREFIX}"
fi
mkdir -p "${PREFIX}/.stamps" "${BUILDROOT}" "${WORK}" "${OUT}"

# clang, not gcc: CLANG64 is a pure LLVM environment and there is no gcc in the
# locked set. The linker is LLD through the clang driver.
export CC=clang CXX=clang++ AR=llvm-ar NM=llvm-nm RANLIB=llvm-ranlib STRIP=llvm-strip
export WINDRES=llvm-windres
# llvm-ar writes deterministic archives (zeroed mtime/uid/gid) by default; stated
# so a toolchain that changed that default fails here rather than silently.
export ARFLAGS=Drc

# `-ffile-prefix-map` matches the path AS THE COMPILER SAW IT, and on Windows
# that is not the POSIX form this script writes. CFLAGS is an ENVIRONMENT
# variable, and MSYS2 does not rewrite the environment (see the PKG_CONFIG_PATH
# note below), so clang received `-ffile-prefix-map=/c/tf-ffbuild=.` — 726 times
# in the first run that reached the verifier — while the sources it was
# compiling were named `C:/tf-ffbuild/...` in its own diagnostics. The map
# matched nothing, and ffmpeg.exe and ffprobe.exe shipped with `C:\tf-ffbuild`
# inside them.
#
# So the Windows spelling is mapped as well. `cygpath -m` rather than
# `cygpath -w` deliberately: -m gives `C:/tf-ffbuild`, which is the spelling
# CMake and clang's diagnostics use, and it carries no backslash. A backslash
# key would travel through autotools `configure`, which re-expands CFLAGS, and
# `\t` would reach clang as a TAB — a flag that maps nothing and cannot be told
# apart from one that matched.
BUILDROOT_M="$(cygpath -m "${BUILDROOT}")"
CACHE_M="$(cygpath -m "${CACHE}")"
PREFIX_M="$(cygpath -m "${PREFIX}")"
MAP="-ffile-prefix-map=${BUILDROOT}=. -ffile-prefix-map=${CACHE}=. -ffile-prefix-map=${PREFIX}=."
MAP="${MAP} -ffile-prefix-map=${BUILDROOT_M}=. -ffile-prefix-map=${CACHE_M}=. -ffile-prefix-map=${PREFIX_M}=."
export CFLAGS="-O2 -g0 ${MAP}"
export CXXFLAGS="${CFLAGS}"
# --no-insert-timestamp is the PE analogue of --build-id=none: without it the
# COFF header carries the moment of the link and two runners can never agree.
# -static resolves libc++, libunwind and libwinpthread into the image, so the
# delivered closure is the operating system's own DLLs and nothing else.
export LDFLAGS="-static -Wl,--no-insert-timestamp"
# pkgconf is a NATIVE Windows program and PKG_CONFIG_PATH is an environment
# variable, not an argument. MSYS2 rewrites POSIX-looking arguments when it execs
# a native binary, but it does not rewrite the environment (PATH aside), so
# "/c/tesserafin-ffmpeg/lib/pkgconfig" reaches pkgconf verbatim and means nothing
# to it. Every package then reports as absent, and FFmpeg's configure blames the
# first one it happens to check:
#
#     ERROR: x265 not found using pkg-config
#
# The separator is ';' for the same reason: a Windows build of pkgconf splits on
# ';', and ':' would be read as part of the drive letter.
PKG_CONFIG_LIB_DIR="$(cygpath -m "${PREFIX}/lib/pkgconfig")"
PKG_CONFIG_SHARE_DIR="$(cygpath -m "${PREFIX}/share/pkgconfig")"
export PKG_CONFIG_PATH="${PKG_CONFIG_LIB_DIR};${PKG_CONFIG_SHARE_DIR}"
export PATH="${PREFIX}/bin:${PATH}"
J="${FF_JOBS}"

PATCH_DIR="${HERE}/patches"
PATCH_SERIES="${PATCH_DIR}/series.txt"

# Applied by unpack() and gitsrc(), so no recipe can forget them and no
# component can be patched without the series naming it. See patches/series.txt
# for what may live there: build system only.
apply_component_patches() { # <component-name> <dir>
    local name="$1" dir="$2" component patch rest count=0
    [[ -f "${PATCH_SERIES}" ]] || return 0
    while read -r component patch rest; do
        [[ -n "${component}" && "${component}" != \#* ]] || continue
        [[ "${component}" == "${name}" ]] || continue
        [[ -f "${PATCH_DIR}/${patch}" ]] \
            || ff_die "patches/series.txt names ${patch}, which does not exist"
        ff_log "  patching ${name} with ${patch}"
        patch -p1 --binary --forward --no-backup-if-mismatch -F0 -d "${dir}" -i "${PATCH_DIR}/${patch}" \
            || ff_die "component patch ${patch} did not apply cleanly to ${name}"
        count=$((count + 1))
    done < "${PATCH_SERIES}"
    [[ "${count}" -eq 0 ]] || ff_log "  ${count} component patch(es) applied to ${name}"
}

unpack() { # <component-name> -> echoes the unpacked directory
    local name="$1" archive dir
    archive="$(find "${CACHE}/archives" -maxdepth 1 -name "${name}-*" -print -quit)"
    [[ -n "${archive}" ]] || ff_die "no fetched archive for ${name}"
    dir="${BUILDROOT}/${name}"
    rm -rf "${dir}"; mkdir -p "${dir}"
    tar --extract --file "${archive}" --directory "${dir}" --strip-components=1
    apply_component_patches "${name}" "${dir}" >&2
    printf '%s\n' "${dir}"
}

gitsrc() { # <component-name> -> echoes a writable copy of the pinned checkout
    local name="$1" dir="${BUILDROOT}/${1}"
    rm -rf "${dir}"
    cp -a "${CACHE}/git/${name}" "${dir}"
    apply_component_patches "${name}" "${dir}" >&2
    printf '%s\n' "${dir}"
}

step() {
    local stamp="${PREFIX}/.stamps/${1// /_}"
    if [[ "${FF_INCREMENTAL:-0}" == "1" && -e "${stamp}" ]]; then
        ff_log "skipping $1 (already built)"; return 1
    fi
    ff_log "building $1"; return 0
}
done_step() { touch "${PREFIX}/.stamps/${1// /_}"; }

# Which components belong to win-x64 is read from ci/ffmpeg/components.json, not
# repeated here. A component the manifest restricts to Linux cannot be built by
# adding a recipe below; it has to be reclassified in the manifest first, where
# the gate can see it.
arch_allows() { # <component-name>
    python3 -c '
import json, sys
manifest, name, arch = sys.argv[1:4]
for c in json.load(open(manifest))["components"]:
    if c["name"] == name:
        allowed = c.get("architectures")
        sys.exit(0 if allowed is None or arch in allowed else 1)
sys.exit(0)
' "${FF_COMPONENTS}" "$1" "${ARCH}" \
        || { ff_log "skipping $1 (not built for ${ARCH})"; return 1; }
}

# Refuses a component the manifest says belongs here but this file has no recipe
# for, so widening the manifest cannot quietly narrow the runtime.
RECIPES=(zlib expat x264 x265 svt-av1 dav1d libvpx lame opus libogg libvorbis
         zimg freetype harfbuzz fribidi fontconfig libunibreak libass
         nv-codec-headers libvpl amf-headers)
# `tr -d '\r'` for the reason given above: without it every name arrives as
# "zlib<CR>", matches nothing in RECIPES, and this gate reports that the script
# has no recipe for any of the 21 components it in fact builds.
mapfile -t EXPECTED < <(python3 -c '
import json, sys
manifest, arch = sys.argv[1:3]
for c in json.load(open(manifest))["components"]:
    allowed = c.get("architectures")
    if allowed is None or arch in allowed:
        print(c["name"])
' "${FF_COMPONENTS}" "${ARCH}" | tr -d '\r')
missing=()
for want in "${EXPECTED[@]}"; do
    found=0
    for have in "${RECIPES[@]}"; do [[ "${want}" == "${have}" ]] && { found=1; break; }; done
    [[ "${found}" -eq 1 ]] || missing+=("${want}")
done
[[ "${#missing[@]}" -eq 0 ]] \
    || ff_die "components.json makes these win-x64-applicable but this script has no recipe: ${missing[*]}"
ff_log "${#EXPECTED[@]} win-x64 components, all with recipes"

# Every component patch must name a component this runtime actually builds, and
# every named file must exist. A patch for a component that is not built is
# either a leftover or a misspelling, and both read as "patched" in the
# provenance while changing nothing.
patch_count=0
while read -r component patch _rest; do
    [[ -n "${component}" && "${component}" != \#* ]] || continue
    found=0
    for want in "${EXPECTED[@]}"; do [[ "${want}" == "${component}" ]] && { found=1; break; }; done
    [[ "${found}" -eq 1 ]] \
        || ff_die "patches/series.txt patches '${component}', which is not a win-x64 component"
    [[ -f "${PATCH_DIR}/${patch}" ]] \
        || ff_die "patches/series.txt names ${patch}, which does not exist"
    patch_count=$((patch_count + 1))
done < "${PATCH_SERIES}"
ff_log "${patch_count} component patch(es) declared"

# =============================================================================
# base
# =============================================================================
if step zlib; then
    d="$(unpack zlib)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --static \
        && make -j"${J}" && make install )
    done_step zlib
fi

if step expat; then
    d="$(unpack expat)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
        --without-docbook --without-examples --without-tests && make -j"${J}" && make install )
    done_step expat
fi

# =============================================================================
# video codecs
# =============================================================================
if step x264; then
    d="$(gitsrc x264)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --enable-static --enable-pic \
        --disable-cli --disable-opencl && make -j"${J}" && make install )
    done_step x264
fi

if step x265; then
    # Ninja explicitly: the generator is an input, and MSYS2's cmake picks a
    # different default depending on which of make and ninja it finds first.
    d="$(unpack x265)"; ( cd "${d}/source" && cmake -S . -B build -G Ninja \
        -DCMAKE_INSTALL_PREFIX="${PREFIX}" -DCMAKE_BUILD_TYPE=Release \
        -DENABLE_SHARED=OFF -DENABLE_CLI=OFF -DENABLE_PIC=ON \
        && cmake --build build -j"${J}" && cmake --install build )
    done_step x265
fi

if step svt-av1; then
    # CMAKE_POLICY_VERSION_MINIMUM, not a patch: SVT-AV1 itself declares 3.16,
    # but it vendors third_party/cpuinfo (2.8.12) and its clog dependency (3.1),
    # and cmake 4.4.2 refuses both. This is CMake's own documented escape hatch
    # for exactly that situation, and it raises a floor rather than changing what
    # any CMakeLists says — which is why it is preferred here over patching two
    # vendored subprojects the pin does not otherwise touch. x265 could not use
    # it: that one also sets removed policies to OLD, which no flag can relax.
    d="$(unpack svt-av1)"; ( cd "${d}" && cmake -S . -B build -G Ninja \
        -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
        -DCMAKE_INSTALL_PREFIX="${PREFIX}" -DCMAKE_BUILD_TYPE=Release \
        -DBUILD_SHARED_LIBS=OFF -DBUILD_APPS=OFF -DBUILD_TESTING=OFF \
        && cmake --build build -j"${J}" && cmake --install build )
    done_step svt-av1
fi

if step dav1d; then
    d="$(unpack dav1d)"; ( cd "${d}" && meson setup build --prefix="${PREFIX}" --libdir=lib \
        --buildtype=release --default-library=static -Denable_tools=false -Denable_tests=false \
        && ninja -C build -j"${J}" && ninja -C build install )
    done_step dav1d
fi

if step libvpx; then
    # --target is stated rather than autodetected: libvpx's detector reads
    # `uname` and would classify this shell as a generic gnu target, which
    # selects the wrong assembler ABI for Win64.
    d="$(unpack libvpx)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" \
        --target=x86_64-win64-gcc --disable-shared --enable-static \
        --enable-pic --disable-examples --disable-tools --disable-docs --disable-unit-tests \
        --enable-vp8 --enable-vp9 \
        && make -j"${J}" && make install )
    done_step libvpx
fi

# =============================================================================
# audio
# =============================================================================
if step lame; then
    d="$(unpack lame)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
        --enable-nasm --disable-frontend && make -j"${J}" && make install )
    done_step lame
fi

if step opus; then
    d="$(unpack opus)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
        --disable-doc --disable-extra-programs && make -j"${J}" && make install )
    done_step opus
fi

if step libogg; then
    d="$(unpack libogg)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
        && make -j"${J}" && make install )
    done_step libogg
fi

if step libvorbis; then
    d="$(unpack libvorbis)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
        --disable-docs --disable-examples --disable-oggtest && make -j"${J}" && make install )
    done_step libvorbis
fi

# =============================================================================
# filters: zimg and the subtitle stack
# =============================================================================
if step zimg; then
    d="$(gitsrc zimg)"; ( cd "${d}" && ./autogen.sh \
        && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
        && make -j"${J}" && make install )
    done_step zimg
fi

# Same two-pass dance as the Linux runtime, and for the same reason: freetype and
# harfbuzz are mutually dependent and libass needs the shaping-aware freetype.
if step "freetype (pass 1, no harfbuzz)"; then
    d="$(unpack freetype)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
        --with-harfbuzz=no --with-brotli=no --with-bzip2=no --with-png=no && make -j"${J}" && make install )
    done_step "freetype (pass 1, no harfbuzz)"
fi

if step harfbuzz; then
    d="$(unpack harfbuzz)"; ( cd "${d}" && meson setup build --prefix="${PREFIX}" --libdir=lib \
        --buildtype=release --default-library=static \
        -Dfreetype=enabled -Dglib=disabled -Dgobject=disabled -Dcairo=disabled -Dicu=disabled \
        -Dtests=disabled -Ddocs=disabled -Dbenchmark=disabled -Dutilities=disabled \
        && ninja -C build -j"${J}" && ninja -C build install )
    done_step harfbuzz
fi

if step "freetype (pass 2, with harfbuzz)"; then
    d="$(unpack freetype)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
        --with-harfbuzz=yes --with-brotli=no --with-bzip2=no --with-png=no && make -j"${J}" && make install )
    done_step "freetype (pass 2, with harfbuzz)"
fi

if step fribidi; then
    d="$(unpack fribidi)"; ( cd "${d}" && meson setup build --prefix="${PREFIX}" --libdir=lib \
        --buildtype=release --default-library=static -Ddocs=false -Dtests=false -Dbin=false \
        && ninja -C build -j"${J}" && ninja -C build install )
    # A static fribidi on Windows needs FRIBIDI_LIB_STATIC in its consumers'
    # cflags, or every fribidi symbol is emitted as a DLL import and the link
    # fails with undefined references that name no missing library.
    sed -i 's/^Cflags:/Cflags: -DFRIBIDI_LIB_STATIC/' "${PREFIX}/lib/pkgconfig/fribidi.pc"
    done_step fribidi
fi

if step fontconfig; then
    d="$(unpack fontconfig)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" \
        --disable-shared --enable-static --disable-docs --disable-nls \
        --with-expat="${PREFIX}" && make -j"${J}" && make install )
    done_step fontconfig
fi

if step libunibreak; then
    d="$(unpack libunibreak)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
        && make -j"${J}" && make install )
    done_step libunibreak
fi

if step libass; then
    # The system font provider on Windows is DirectWrite, which libass reaches
    # through the OS. Unlike the Linux runtime this is NOT disabled: fontconfig
    # is still built and used, and DirectWrite is a second provider that costs
    # no delivered dependency.
    d="$(unpack libass)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
        --enable-libunibreak && make -j"${J}" && make install )
    done_step libass
fi

# =============================================================================
# hardware SDKs. Headers and dispatchers only; no runtime claim is made here.
# =============================================================================
if step nv-codec-headers; then
    d="$(gitsrc nv-codec-headers)"; ( cd "${d}" && make PREFIX="${PREFIX}" install )
    done_step nv-codec-headers
fi

if arch_allows libvpl && step libvpl; then
    d="$(unpack libvpl)"; ( cd "${d}" && cmake -S . -B build -G Ninja \
        -DCMAKE_INSTALL_PREFIX="${PREFIX}" \
        -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DBUILD_TESTS=OFF -DBUILD_EXAMPLES=OFF \
        -DINSTALL_EXAMPLE_CODE=OFF && cmake --build build -j"${J}" && cmake --install build )
    # The dispatcher is C++; a C consumer linking it statically needs the C++
    # runtime named. CLANG64 is a libc++ environment, not libstdc++.
    printf 'Libs.private: -lc++\n' >> "${PREFIX}/lib/pkgconfig/vpl.pc"
    done_step libvpl
fi

if arch_allows amf-headers && step amf-headers; then
    d="$(gitsrc amf-headers)"; mkdir -p "${PREFIX}/include/AMF"
    cp -a "${d}/amf/public/include/." "${PREFIX}/include/AMF/"
    done_step amf-headers
fi

# =============================================================================
# Normalise the pkg-config files for a libc++ / mingw environment
# =============================================================================
# Every C++ component writes the GNU/Linux runtime into its .pc file. x265 3.6
# emits
#
#     Libs.private: -lstdc++ -lm -lgcc_s -lgcc -lrt -ldl
#
# and CLANG64 has none of stdc++, gcc_s, rt or dl. With
# --pkg-config-flags=--static FFmpeg link-tests those libraries, the test fails,
# and configure reports
#
#     ERROR: x265 not found using pkg-config
#
# which names the library rather than the four that are actually missing.
#
# The substitution is narrow and stated: the C++ runtime is renamed to the one
# this environment has, and four GNU-only libraries whose symbols live in the
# Windows CRT are dropped. Nothing is added, and no .pc gains a library it did
# not ask for.
ff_log "normalising pkg-config files for libc++"
while IFS= read -r pc; do
    before="$(sha256sum "${pc}" | cut -d' ' -f1)"
    # Repeated to a fixed point, not applied once: adjacent entries share the
    # space between them, so a single pass over `-lgcc_s -lgcc` consumes the
    # separator with the first match and leaves the second behind. Measured, not
    # feared — one pass over x265's real .pc leaves `-lgcc -lgcc -ldl`.
    for _ in 1 2 3 4; do
        # `-l-l:libunwind.a` is not a typo here, it is what x265 3.6 writes.
        # Its pkg-config generator prefixes "-l" to every entry of
        # CMAKE_CXX_IMPLICIT_LINK_LIBRARIES, and on CLANG64 one of those entries
        # is already the linker argument `-l:libunwind.a`. lld then reports
        #
        #     unable to find library -l-l:libunwind.a
        #
        # and FFmpeg reports the whole component as not found.
        sed -i -E 's/-l-l/-l/g; s/-lstdc\+\+/-lc++/g; s/(^|[[:space:]])-l(gcc_s|gcc|rt|dl)([[:space:]]|$)/\1\3/g' "${pc}"
    done
    sed -i -E 's/[[:space:]]+/ /g; s/[[:space:]]+$//' "${pc}"
    after="$(sha256sum "${pc}" | cut -d' ' -f1)"
    [[ "${before}" == "${after}" ]] || ff_log "  rewrote $(basename "${pc}")"
done < <(find "${PREFIX}/lib/pkgconfig" "${PREFIX}/share/pkgconfig" -name '*.pc' 2>/dev/null | sort)

# A leftover reference is a link failure thirty minutes later, reported against
# the wrong library, so it stops the build here instead.
if leftovers="$(grep -lE -- '(-l-l|-l(stdc\+\+|gcc_s|gcc|rt|dl)([[:space:]]|$))' "${PREFIX}"/lib/pkgconfig/*.pc 2>/dev/null)"; then
    [[ -z "${leftovers}" ]] || ff_die "these pkg-config files still name a GNU-only runtime: ${leftovers}"
fi

# Ask pkg-config directly, before FFmpeg does. Its answer here is unambiguous;
# FFmpeg's is not — configure reports "<library> not found using pkg-config"
# whether the package is missing, the path is unreadable or the link test failed,
# and those have nothing to do with each other.
ff_log "pkg-config sees:"
pkgconfig_missing=()
# lame is absent from this list on purpose: it ships no .pc file at all, and
# FFmpeg detects it with a plain link test rather than through pkg-config.
for mod in x264 x265 SvtAv1Enc dav1d vpx opus vorbis zimg libass freetype2 fontconfig harfbuzz fribidi vpl; do
    if version="$(pkg-config --modversion "${mod}" 2>/dev/null)"; then
        ff_log "  ${mod} ${version}"
    else
        pkgconfig_missing+=("${mod}")
    fi
done
[[ "${#pkgconfig_missing[@]}" -eq 0 ]] \
    || ff_die "pkg-config cannot see: ${pkgconfig_missing[*]} (PKG_CONFIG_PATH=${PKG_CONFIG_PATH})"

# Which archives still carry the build root, and what the bytes around it look
# like. REPORTED, NOT ENFORCED: the contract is about the delivered binaries,
# not the intermediate archives. ci/ffmpeg/verify-runtime.sh has always scanned
# exactly two files, ffmpeg and ffprobe, and verify-runtime.py scans the
# delivered binaries; neither has ever looked inside a static archive, and a
# string in an archive only matters if the linker keeps it.
#
# It is printed because it localises a failure that is otherwise reported
# against a statically linked ffmpeg.exe fifty minutes later, naming none of the
# twenty-one components that could have contributed it. The context bytes are
# the discriminator: a path with a source file after it is a compiled __FILE__
# that -ffile-prefix-map can reach, while a bare path is a generated config or
# resource string that no compiler flag will ever rewrite.
ff_log "scanning the static archives for the build root (report only)"
python3 - "${PREFIX}/lib" "${BUILDROOT}" "$(cygpath -m "${BUILDROOT}")" "$(cygpath -w "${BUILDROOT}")" <<'PYSCAN' >&2 || true
import pathlib, sys

libdir = pathlib.Path(sys.argv[1])
needles = [n for n in dict.fromkeys(sys.argv[2:]) if n]
hits = 0
for archive in sorted(libdir.glob("*.a")):
    blob = archive.read_bytes()
    for needle in needles:
        raw = needle.encode()
        at = blob.find(raw)
        if at < 0:
            continue
        hits += 1
        ctx = blob[at:at + 90].split(b"\x00")[0].decode("utf-8", "replace").strip()
        print(f"ffmpeg-runtime:   {archive.name} carries {needle!r}: {ctx}")
print(f"ffmpeg-runtime: {hits} archive/spelling pair(s) carry the build root"
      if hits else "ffmpeg-runtime: no static archive carries the build root")
PYSCAN

# =============================================================================
# FFmpeg
# =============================================================================
FFSRC="${BUILDROOT}/ffmpeg"
mapfile -t FLAGS < <(grep -hvE '^\s*(#|$)' "${WIN_FLAGS_FILE}")

if step "FFmpeg ${FF_FFMPEG_BASELINE} (${FF_BUILD_REVISION}) win-x64"; then
rm -rf "${FFSRC}"; cp -a "${CACHE}/git/jellyfin-ffmpeg" "${FFSRC}"

# The fork ships its 95 changes as a quilt series rather than pre-applied.
# Which of them win-x64 applies is NOT decided here: it is computed from
# ci/ffmpeg/fork-patches.json, base decision plus declared platform exceptions.
# Hard-coding "all 95" here is what let the repository's own exclusion policy and
# this build disagree while both looked correct in isolation.
#
# The two failure directions are separated deliberately. A patch this platform is
# supposed to skip but applies, and a patch it is supposed to apply but skips,
# are different defects and get different messages.
# `tr -d '\r'` for the reason given at the top of this file: Python on Windows
# writes CRLF on text stdout, and without this every patch name arrives as
# "0001-...patch<CR>", matches nothing in the series read from the fork's own
# file, and this build reports that the catalogue classifies none of the 95
# patches it is about to apply.
mapfile -t WIN_PATCH_PLAN < <(python3 - "${FF_REPO_ROOT}/ci/ffmpeg/fork-patches.json" <<'PY' | tr -d '\r'
import json, sys
d = json.load(open(sys.argv[1]))
for e in d["patches"]:
    applied = e["applied"]
    exception = "-"
    for exc in e.get("platformExceptions", []):
        if exc["platform"] == "win-x64":
            applied, exception = exc["applied"], "exception"
    print(f"{e['patch']}\t{'apply' if applied else 'skip'}\t{exception}")
PY
) || ff_die "could not read the win-x64 patch decision from ci/ffmpeg/fork-patches.json"

declare -A WIN_PATCH_DECISION=() WIN_PATCH_EXCEPTION=()
WIN_PATCHES_EXPECTED=0
for line in "${WIN_PATCH_PLAN[@]}"; do
    IFS=$'\t' read -r p decision exception <<<"${line}"
    WIN_PATCH_DECISION["${p}"]="${decision}"
    WIN_PATCH_EXCEPTION["${p}"]="${exception}"
    [[ "${decision}" == "apply" ]] && WIN_PATCHES_EXPECTED=$((WIN_PATCHES_EXPECTED + 1))
done
ff_log "win-x64 patch decision: ${WIN_PATCHES_EXPECTED} of ${#WIN_PATCH_PLAN[@]} to apply"

(
    cd "${FFSRC}"
    applied=0
    skipped=0
    exceptions=0
    : > "${OUT}/patch-decisions.tsv"
    while read -r p; do
        [[ -n "${p}" ]] || continue
        decision="${WIN_PATCH_DECISION[${p}]:-}"
        [[ -n "${decision}" ]] \
            || ff_die "the fork series carries ${p}, which ci/ffmpeg/fork-patches.json does not classify"
        exception="${WIN_PATCH_EXCEPTION[${p}]:-'-'}"
        printf '%s\t%s\t%s\n' "${p}" "${decision}" "${exception}" >> "${OUT}/patch-decisions.tsv"
        [[ "${exception}" == "exception" ]] && exceptions=$((exceptions + 1))
        if [[ "${decision}" != "apply" ]]; then
            # Skipping is a decision this platform DECLARED, not a failure. It is
            # counted and recorded so that "win-x64 skipped a patch" can never be
            # indistinguishable from "win-x64 applied everything".
            ff_log "  not applying ${p}: win-x64 excludes it"
            skipped=$((skipped + 1))
            continue
        fi
        # --forward and -F0: a patch that only applies approximately is a patch
        # applying to the wrong place. The series is pinned by the same commit as
        # the tree, so anything but a clean apply means the pin moved.
        # --binary: the fork's .gitattributes marks tests/ref/fate/* as -text,
        # so several reference files legitimately contain CRLF bytes. GNU patch
        # on MSYS2 then refuses an LF-context hunk against them with
        #
        #     Hunk #1 FAILED at 33 (different line endings).
        #
        # which stopped 0059 on the runner while all 95 applied on Linux.
        # --binary makes patch treat both sides as bytes, which is what the
        # attribute already says they are. Verified to be a no-op on Linux: the
        # tree after applying all 95 with and without it hashes identically.
        patch -p1 --binary --forward --no-backup-if-mismatch -F0 -i "debian/patches/${p}" \
            >> "${OUT}/patches.log" 2>&1 \
            || { tail -20 "${OUT}/patches.log" >&2; ff_die "patch ${p} did not apply cleanly"; }
        applied=$((applied + 1))
    done < debian/patches/series
    series_length="$(grep -cvE '^\s*$' debian/patches/series)"
    [[ "${series_length}" -eq "${#WIN_PATCH_PLAN[@]}" ]] \
        || ff_die "the fork ships ${series_length} patches but ci/ffmpeg/fork-patches.json classifies ${#WIN_PATCH_PLAN[@]}"
    # The two directions are separate failures. Applying fewer than the platform
    # decision means a patch was dropped silently; applying more means one the
    # policy refuses was taken anyway. Both were invisible while this asserted a
    # literal 95.
    [[ "${applied}" -le "${WIN_PATCHES_EXPECTED}" ]] \
        || ff_die "applied ${applied} patches; the win-x64 decision in ci/ffmpeg/fork-patches.json is ${WIN_PATCHES_EXPECTED}"
    [[ "${applied}" -eq "${WIN_PATCHES_EXPECTED}" ]] \
        || ff_die "applied ${applied} of the ${WIN_PATCHES_EXPECTED} patches win-x64 declares; ${skipped} were skipped"
    [[ "${applied}" -eq "${series_length}" || "${skipped}" -gt 0 ]] \
        || ff_die "applied ${applied} of ${series_length} with nothing recorded as skipped"
    ff_log "applied ${applied}/${series_length} fork patches, zero fuzz (${skipped} excluded, ${exceptions} declared platform exception(s))"

    # The series having run is not the same as the series having landed.
    grep -q 'tonemapx' libavfilter/allfilters.c \
        || ff_die "the patch series applied but tonemapx is absent: the fork baseline did not land"
    grep -q 'alphasrc' libavfilter/allfilters.c \
        || ff_die "the patch series applied but alphasrc is absent: the fork baseline did not land"
    printf '%s\n' "${applied}" > "${OUT}/patches-applied.txt"
    printf '%s\n' "${skipped}" > "${OUT}/patches-skipped.txt"
)
(
    cd "${FFSRC}"
    # Deliberately NOT ${CFLAGS}: FFmpeg records its configure line verbatim and
    # echoes it from -buildconf, so every -ffile-prefix-map argument would put
    # the path it maps away straight back into the shipped binary. FFmpeg builds
    # in-tree and its __FILE__ values are already relative.
    FF_CFLAGS="-O2 -g0 -I${PREFIX}/include"
    # FFmpeg's configure does NOT read $CC, $AR or $STRIP from the environment:
    # they are CMDLINE_SET options and it defaults cc to `gcc`. CLANG64 is a pure
    # LLVM environment with no gcc at all, so without these the very first probe
    # reports
    #
    #     gcc is unable to create an executable file.
    #
    # after every component has already been built. Naming the toolchain here
    # also puts it verbatim into what -buildconf reports, so the delivered
    # build-configuration.txt states which compiler produced the binary.
    ./configure \
        "${FLAGS[@]}" \
        --cc=clang --cxx=clang++ --ld=clang++ \
        --ar=llvm-ar --nm=llvm-nm --ranlib=llvm-ranlib --strip=llvm-strip \
        --windres=llvm-windres \
        --extra-version="tesserafin.1" \
        --pkg-config-flags=--static \
        --extra-cflags="${FF_CFLAGS}" \
        --extra-cxxflags="${FF_CFLAGS}" \
        --extra-ldflags="-static -Wl,--no-insert-timestamp -L${PREFIX}/lib" \
        > "${OUT}/configure.log" 2>&1 || {
            tail -80 "${OUT}/configure.log" >&2
            # configure.log holds the verdict; ffbuild/config.log holds the
            # REASON — the failing compile or link command and its output.
            # Without this the log says "<library> not found using pkg-config"
            # and every distinct cause looks identical.
            if [[ -f ffbuild/config.log ]]; then
                ff_log "last 60 lines of ffbuild/config.log"
                tail -60 ffbuild/config.log >&2
            fi
            # configure dies on the FIRST option it does not recognise, roughly
            # thirty minutes into a clean build, and names only that one. Ask it
            # about every flag individually before giving up, so one round trip
            # reports the whole list instead of one flag per round trip.
            #
            # The sentinel is the trick: options are processed in order, so a run
            # that dies on the sentinel proves the flag before it was accepted.
            # Captured into a variable, NOT piped into grep -q. `set -o pipefail`
            # is in force, grep -q exits as soon as it matches, configure dies of
            # SIGPIPE, and the pipeline returns 141 — so the piped form reported
            # every flag as rejected and hid the real failure underneath a wall
            # of false ones. It did exactly that on run 7.
            ff_log "asking configure about each flag individually"
            for f in "${FLAGS[@]}"; do
                [[ "${f}" == --prefix=* ]] && continue
                probe="$(./configure "${f}" --enable-zzz-not-a-real-option 2>&1 || true)"
                [[ "${probe}" == *zzz-not-a-real-option* ]] || ff_log "  REJECTED: ${f}"
            done
            ff_die "FFmpeg configure failed"
        }
    make -j"${J}" > "${OUT}/make.log" 2>&1 || { tail -80 "${OUT}/make.log" >&2; ff_die "FFmpeg build failed"; }
    make install DESTDIR="${WORK}" >> "${OUT}/make.log" 2>&1
)
    done_step "FFmpeg ${FF_FFMPEG_BASELINE} (${FF_BUILD_REVISION}) win-x64"
fi

# =============================================================================
# stage the runtime
# =============================================================================
STAGE="${OUT}/tesserafin-ffmpeg-${FF_BUILD_REVISION}-${ARCH}"
rm -rf "${STAGE}"; mkdir -p "${STAGE}/bin"
# ${WORK} is a DESTDIR, so the installed tree sits under it at the prefix path.
INSTALLED="${WORK}${PREFIX}"
[[ -d "${INSTALLED}/bin" ]] || ff_die "nothing was installed at ${INSTALLED}"
for exe in ffmpeg ffprobe; do
    install -m 0755 "${INSTALLED}/bin/${exe}.exe" "${STAGE}/bin/${exe}.exe"
done
# llvm-strip, not the host strip: the only binutils in the locked set is the MSYS
# one, which does not understand a CLANG64 PE.
llvm-strip --strip-unneeded "${STAGE}/bin/ffmpeg.exe" "${STAGE}/bin/ffprobe.exe"

# Anything else the install produced that is not one of the two programs is
# reported, not silently dropped: a shared DLL appearing here would mean the
# static link did not hold and the delivered closure is incomplete.
extra="$(find "${INSTALLED}/bin" -maxdepth 1 -type f ! -name 'ffmpeg.exe' ! -name 'ffprobe.exe' -printf '%f\n' | sort || true)"
[[ -z "${extra}" ]] || ff_log "note: install produced additional files in bin: ${extra}"
printf '%s\n' "${extra}" > "${OUT}/install-extra-files.txt"

ff_log "staged $(du -sh "${STAGE}" | cut -f1) at ${STAGE}"
printf '%s\n' "${STAGE}"

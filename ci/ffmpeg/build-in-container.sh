#!/usr/bin/env bash
# The actual Tesserafin FFmpeg runtime build (F0 / #229). Runs INSIDE the
# digest-pinned builder image; ci/ffmpeg/build-runtime.sh is what puts it there.
#
# Usage: ci/ffmpeg/build-in-container.sh --cache DIR --out DIR --arch RID
#
# Every component is built static into one prefix, then FFmpeg links against
# that prefix. Nothing is taken from the host distribution except the toolchain
# and glibc, which come from the pinned image and are therefore inputs.
#
# Determinism rules applied uniformly:
#   * SOURCE_DATE_EPOCH from the pinned FFmpeg baseline, never the clock;
#   * -ffile-prefix-map so no build path is embedded;
#   * -Wl,--build-id=none so no build-id varies;
#   * a FIXED job count, because parallelism must not be an input;
#   * LC_ALL=C and TZ=UTC so no locale or zone leaks into generated files.

set -euo pipefail

# shellcheck source=ci/ffmpeg/lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

CACHE=""; OUT=""; ARCH=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --cache) CACHE="$2"; shift 2 ;;
        --out)   OUT="$2"; shift 2 ;;
        --arch)  ARCH="$2"; shift 2 ;;
        *) ff_die "unknown argument: $1" ;;
    esac
done
[[ -d "${CACHE}" ]] || ff_die "--cache must be an existing source cache"
[[ -n "${OUT}" ]]   || ff_die "--out is required"
[[ -n "${ARCH}" ]]  || ff_die "--arch is required"

ff_load_manifest
TRIPLET="$(ff_arch_triplet "${ARCH}")"
[[ "$(uname -m)" == "${TRIPLET}" ]] \
    || ff_die "this host is $(uname -m); ${ARCH} must be built on a native ${TRIPLET} machine"

SOURCE_DATE_EPOCH="$(ff_source_date_epoch)"
export SOURCE_DATE_EPOCH
export LC_ALL=C LANG=C TZ=UTC

# Every path here is a FIXED string, never mktemp. Two runs must agree on the
# bytes, and a random directory name can reach a binary through a debug string,
# a generated config or a __FILE__ that escaped -ffile-prefix-map.
#
# PREFIX is the REAL install prefix, not a scratch directory, because several
# dependencies compile their prefix into the shipped binary and then read from
# it at RUNTIME on the user's machine:
#
#   * OpenSSL bakes in OPENSSLDIR and reads openssl.cnf from it;
#   * libvpl bakes the dispatcher's search path and dlopen()s libmfx-gen.so from it;
#   * fontconfig bakes its cache and config directories.
#
# With a /tmp prefix all three resolve into a world-writable directory on the
# recipient's system. The dlopen one is arbitrary code execution: anyone who can
# create /tmp/<prefix>/lib/libmfx-gen.so.1.2 gets it loaded into ffmpeg. /opt is
# root-owned on every declared distribution, so the same paths become inert.
# The builder image pre-creates it writable so the container can still run as
# the invoking user and leave the output owned by the caller.
export PREFIX=/opt/tesserafin-ffmpeg
BUILDROOT=/tmp/tf-ffbuild
WORK=/tmp/tf-ffinstall

# FF_INCREMENTAL is a DEVELOPMENT convenience: it keeps already-built components
# so a failure late in the list does not rebuild the twelve that already
# succeeded. CI never sets it, and it must never be set in the workflow — the
# reproducibility evidence is only meaningful from a clean tree.
if [[ "${FF_INCREMENTAL:-0}" == "1" ]]; then
    ff_log "INCREMENTAL build: completed components are reused. Not valid for reproducibility evidence."
else
    # PREFIX itself is created by the builder image and owned by root, so the
    # build can write into it but cannot remove it. Only its CONTENTS belong to
    # the build. Removing the directory fails with EACCES on /opt, which killed
    # every clean build while incremental ones — which never take this branch —
    # kept working.
    rm -rf "${BUILDROOT}" "${WORK}"
    find "${PREFIX}" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
fi
mkdir -p "${PREFIX}/.stamps" "${BUILDROOT}" "${WORK}" "${OUT}"

export CFLAGS="-O2 -g0 -fPIC -ffile-prefix-map=${BUILDROOT}=. -ffile-prefix-map=${PREFIX}=. -ffile-prefix-map=${CACHE}=."
export CXXFLAGS="${CFLAGS}"
export LDFLAGS="-Wl,--build-id=none"
export PKG_CONFIG_PATH="${PREFIX}/lib/pkgconfig:${PREFIX}/lib64/pkgconfig:${PREFIX}/share/pkgconfig"
export PATH="${PREFIX}/bin:${PATH}"
J="${FF_JOBS}"

unpack() { # <component-name> -> echoes the unpacked directory
    local name="$1" archive dir
    archive="$(find "${CACHE}/archives" -maxdepth 1 -name "${name}-*" -print -quit)"
    [[ -n "${archive}" ]] || ff_die "no fetched archive for ${name}"
    dir="${BUILDROOT}/${name}"
    rm -rf "${dir}"; mkdir -p "${dir}"
    tar --extract --file "${archive}" --directory "${dir}" --strip-components=1
    printf '%s\n' "${dir}"
}

gitsrc() { # <component-name> -> echoes a writable copy of the pinned checkout
    local name="$1" dir="${BUILDROOT}/${1}"
    rm -rf "${dir}"
    cp -a "${CACHE}/git/${name}" "${dir}"
    printf '%s\n' "${dir}"
}

# Returns non-zero when the component is already built, so a recipe reads
# `step x && ( ...build... )` and is skipped wholesale on a resumed run.
step() {
    local stamp="${PREFIX}/.stamps/${1// /_}"
    if [[ "${FF_INCREMENTAL:-0}" == "1" && -e "${stamp}" ]]; then
        ff_log "skipping $1 (already built)"
        return 1
    fi
    ff_log "building $1"
    return 0
}
done_step() { touch "${PREFIX}/.stamps/${1// /_}"; }

# A component that components.json restricts to a subset of architectures. The
# restriction is read from the manifest rather than repeated here, so the gate,
# the fetcher and the build cannot disagree about which architectures a
# component belongs to.
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

# =============================================================================
# base
# =============================================================================
if step zlib; then
    d="$(unpack zlib)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --static && make -j"${J}" && make install )
    done_step zlib
fi

if step expat; then
    d="$(unpack expat)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
        --without-docbook --without-examples --without-tests && make -j"${J}" && make install )
    done_step expat
fi

if step openssl; then
    # no-docs only exists from 3.1; 3.0.x rejects it. install_sw skips docs anyway.
    d="$(unpack openssl)"; ( cd "${d}" && ./Configure --prefix="${PREFIX}" --libdir=lib no-shared no-tests \
        --openssldir="${PREFIX}/ssl" && make -j"${J}" && make install_sw )
    done_step openssl
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
    d="$(unpack x265)"
    "${FF_REPO_ROOT}/ci/ffmpeg/build-x265.sh" "${d}" "${PREFIX}" "${J}"
    done_step x265
fi

if step svt-av1; then
    d="$(unpack svt-av1)"; ( cd "${d}" && cmake -S . -B build \
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
    d="$(unpack libvpx)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
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
    # Submodules came down with the source cache; there is no network here.
    d="$(gitsrc zimg)"; ( cd "${d}" && ./autogen.sh \
        && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
        && make -j"${J}" && make install )
    done_step zimg
fi

# freetype and harfbuzz are mutually dependent. Build freetype without
# harfbuzz, build harfbuzz against it, then rebuild freetype with harfbuzz so
# libass gets the shaping-aware freetype. Two passes, both deterministic.
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
    done_step fribidi
fi

if step fontconfig; then
    # autotools, not meson: 2.15 requires meson >= 0.60 and the pinned builder
    # carries 0.56. 2.14.2 is the last release that still ships a configure
    # script, which keeps the toolchain an input rather than something this
    # build has to upgrade.
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
    d="$(unpack libass)"; ( cd "${d}" && ./configure --prefix="${PREFIX}" --disable-shared --enable-static \
        --disable-require-system-font-provider && make -j"${J}" && make install )
    done_step libass
fi

# =============================================================================
# hardware: the VAAPI stack and header-only SDKs
# =============================================================================
# libva is pinned at the OLDEST release in the declared matrix and linked
# normally. Upstream generates an implib trampoline here; that is precisely the
# construct that turns a missing symbol into assert(0), and it is not used.
if step libdrm; then
    d="$(unpack libdrm)"; ( cd "${d}" && meson setup build --prefix="${PREFIX}" --libdir=lib \
        --buildtype=release --default-library=static \
        -Dintel=disabled -Dradeon=disabled -Damdgpu=disabled -Dnouveau=disabled -Dvmwgfx=disabled \
        -Dcairo-tests=disabled -Dman-pages=disabled -Dvalgrind=disabled -Dtests=false \
        -Dlibkms=disabled 2>/dev/null \
        || meson setup build --prefix="${PREFIX}" --libdir=lib --buildtype=release \
           --default-library=static -Dcairo-tests=disabled -Dman-pages=disabled -Dtests=false; \
        ninja -C build -j"${J}" && ninja -C build install )
    done_step libdrm
fi

if step libva; then
    # driverdir is the single most important option here and it has no sensible
    # default for a BUNDLED libva. Left alone, meson derives it from --prefix, so
    # the shipped libva looks for VA drivers in /opt/tesserafin-ffmpeg/lib/dri —
    # a directory this project never installs anything into. The result is
    # va_openDriver() returning -1 on a machine that has a perfectly good driver,
    # with -hwaccels still listing vaapi and looking healthy.
    #
    # The list below is every location the four declared distributions put a VA
    # driver, for both architectures. libva walks it in order and also honours
    # LIBVA_DRIVERS_PATH ahead of it, so a user with a driver somewhere unusual
    # can still point at it.
    LIBVA_DRIVERDIR='/usr/lib/x86_64-linux-gnu/dri:/usr/lib/aarch64-linux-gnu/dri:/usr/lib64/dri:/usr/lib64/dri-freeworld:/usr/lib/dri:/usr/local/lib/dri'
    d="$(unpack libva)"; ( cd "${d}" && meson setup build --prefix="${PREFIX}" --libdir=lib \
        --buildtype=release --default-library=static \
        -Ddriverdir="${LIBVA_DRIVERDIR}" \
        -Dwith_x11=no -Dwith_glx=no -Dwith_wayland=no -Dwith_win32=no -Denable_docs=false \
        && ninja -C build -j"${J}" && ninja -C build install )
    done_step libva
fi

if step nv-codec-headers; then
    d="$(gitsrc nv-codec-headers)"; ( cd "${d}" && make PREFIX="${PREFIX}" install )
    done_step nv-codec-headers
fi

if arch_allows libvpl && step libvpl; then
    d="$(unpack libvpl)"; ( cd "${d}" && cmake -S . -B build -DCMAKE_INSTALL_PREFIX="${PREFIX}" \
        -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DBUILD_TESTS=OFF -DBUILD_EXAMPLES=OFF \
        -DINSTALL_EXAMPLE_CODE=OFF && cmake --build build -j"${J}" && cmake --install build )
    done_step libvpl
fi

if arch_allows amf-headers && step amf-headers; then
    d="$(gitsrc amf-headers)"; mkdir -p "${PREFIX}/include/AMF"; cp -a "${d}/amf/public/include/." "${PREFIX}/include/AMF/"
    done_step amf-headers
fi

if step opencl-headers; then
    d="$(gitsrc opencl-headers)"; mkdir -p "${PREFIX}/include/CL"; cp -a "${d}/CL/." "${PREFIX}/include/CL/"
    done_step opencl-headers
fi

if step opencl-icd-loader; then
    # FFmpeg's opencl check links against -lOpenCL, so headers alone are not
    # enough. Upstream solves this with an implib trampoline; this links the
    # Khronos ICD loader statically instead. Vendor discovery still happens at
    # runtime through /etc/OpenCL/vendors, so a machine with no OpenCL driver
    # gets a controlled "no platforms" result rather than an abort.
    d="$(gitsrc opencl-icd-loader)"; ( cd "${d}" && cmake -S . -B build \
        -DCMAKE_INSTALL_PREFIX="${PREFIX}" -DCMAKE_BUILD_TYPE=Release \
        -DBUILD_SHARED_LIBS=OFF -DBUILD_TESTING=OFF \
        -DOPENCL_ICD_LOADER_HEADERS_DIR="${PREFIX}/include" \
        && cmake --build build -j"${J}" && cmake --install build )
    done_step opencl-icd-loader
fi

# =============================================================================
# Vulkan: headers, shader compiler, libplacebo (linux only; #342)
# =============================================================================
# Headers only. FFmpeg's vulkan check is a preprocessor test against vulkan.pc,
# and both FFmpeg and libplacebo (vk-proc-addr disabled) load the host's
# libvulkan.so.1 at run time, so the runtime gains no DT_NEEDED entry.
if arch_allows vulkan-headers && step vulkan-headers; then
    d="${CACHE}/git/vulkan-headers"
    mkdir -p "${PREFIX}/include" "${PREFIX}/share/vulkan" "${PREFIX}/lib/pkgconfig"
    cp -a "${d}/include/vulkan" "${d}/include/vk_video" "${PREFIX}/include/"
    cp -a "${d}/registry" "${PREFIX}/share/vulkan/"
    ref="$(python3 -c 'import json,sys; print(next(c["ref"] for c in json.load(open(sys.argv[1]))["components"] if c["name"] == "vulkan-headers"))' "${FF_COMPONENTS}")"
    # shellcheck disable=SC2016 # ${prefix} is pkg-config syntax, written literally
    printf 'prefix=%s\nincludedir=${prefix}/include\n\nName: vulkan\nDescription: Vulkan (headers only)\nVersion: %s\nCflags: -I${includedir}\n' \
        "${PREFIX}" "${ref#v}" > "${PREFIX}/lib/pkgconfig/vulkan.pc"
    done_step vulkan-headers
fi

if arch_allows shaderc && step shaderc; then
    # glslang, SPIRV-Tools and SPIRV-Headers are separate pins, built in-tree
    # where shaderc expects them, into one static shaderc_combined.
    d="$(gitsrc shaderc)"
    for dep in glslang spirv-tools spirv-headers; do
        rm -rf "${d}/third_party/${dep}"; cp -a "${CACHE}/git/${dep}" "${d}/third_party/${dep}"
    done
    ( cd "${d}" && cmake -S . -B build -G Ninja \
        -DCMAKE_INSTALL_PREFIX="${PREFIX}" -DCMAKE_INSTALL_LIBDIR=lib -DCMAKE_BUILD_TYPE=Release \
        -DBUILD_SHARED_LIBS=OFF -DSHADERC_SKIP_TESTS=ON -DSHADERC_SKIP_EXAMPLES=ON \
        -DSHADERC_SKIP_COPYRIGHT_CHECK=ON -DENABLE_GLSLANG_BINARIES=OFF -DENABLE_CTEST=OFF \
        -DSPIRV_SKIP_EXECUTABLES=ON -DSPIRV_SKIP_TESTS=ON -DSPIRV_TOOLS_BUILD_STATIC=ON \
        && ninja -C build -j"${J}" && ninja -C build install )
    # Static only, and "shaderc" must resolve to the combined archive plus the
    # C++ runtime, or every consumer links half a compiler.
    rm -f "${PREFIX}"/lib/libshaderc_shared.so* "${PREFIX}"/bin/glslc
    sed -i 's/^Libs: \(.*\)$/Libs: \1 -lstdc++/' "${PREFIX}/lib/pkgconfig/shaderc_combined.pc"
    cp "${PREFIX}/lib/pkgconfig/shaderc_combined.pc" "${PREFIX}/lib/pkgconfig/shaderc.pc"
    done_step shaderc
fi

if arch_allows libplacebo && step libplacebo; then
    # meson >= 0.63 is required; the builder's is 0.56, so the pinned meson
    # checkout runs this one step.
    d="$(gitsrc libplacebo)"; ( cd "${d}" && PYTHONDONTWRITEBYTECODE=1 python3 "${CACHE}/git/meson/meson.py" setup build \
        --prefix="${PREFIX}" --libdir=lib --buildtype=release --default-library=static \
        -Dvulkan=enabled -Dvk-proc-addr=disabled -Dvulkan-registry="${PREFIX}/share/vulkan/registry/vk.xml" \
        -Dshaderc=enabled -Dglslang=disabled -Dopengl=disabled -Dd3d11=disabled -Dlcms=disabled \
        -Ddovi=enabled -Dlibdovi=disabled -Dunwind=disabled -Dxxhash=disabled \
        -Ddemos=false -Dtests=false -Dbench=false -Dfuzz=false \
        && ninja -C build -j"${J}" && ninja -C build install )
    printf 'Libs.private: -lstdc++\n' >> "${PREFIX}/lib/pkgconfig/libplacebo.pc"
    done_step libplacebo
fi

# =============================================================================
# FFmpeg
# =============================================================================
FFSRC="${BUILDROOT}/ffmpeg"

# libva builds shared only — its meson.build calls shared_library() outright and
# ignores default_library — so it cannot be folded into the binary the way every
# other component is. It is BUNDLED instead, with an $ORIGIN-relative RUNPATH,
# which is strictly better than linking whatever libva the host happens to have:
# the runtime always gets the pinned libva, and there is no implib trampoline to
# turn a version difference into assert(0). $ORIGIN names no host path, no build
# workspace and no vendor, so the portability gate accepts it.
# The RUNPATH is NOT passed through configure. FFmpeg's configure mangles a
# literal $ in --extra-ldflags — `$ORIGIN` arrived as `/../lib` and `$$ORIGIN`
# as `25ORIGIN/../lib`, both of which produce a binary that cannot find its
# bundled libva while still looking plausible in `readelf`. patchelf sets it
# afterwards, exactly and verifiably.
RPATH_FLAG=''

# The common flags plus whatever this architecture adds. QSV and AMF are x86-64
# vendor stacks and are not in the common set; see ffmpeg-configure.linux-x64.txt.
ARCH_FLAGS_FILE="${FF_REPO_ROOT}/ci/ffmpeg/ffmpeg-configure.${ARCH}.txt"
[[ -f "${ARCH_FLAGS_FILE}" ]] \
    || ff_die "no configure flags declared for ${ARCH} (expected ${ARCH_FLAGS_FILE})"
mapfile -t FLAGS < <(grep -hvE '^\s*(#|$)' "${FF_FLAGS_FILE}" "${ARCH_FLAGS_FILE}")
# --prefix stays the real installed location, so -buildconf records where the
# runtime actually lives and FFmpeg resolves its datadir against a path that
# will exist. Staging happens through DESTDIR instead.

if step "FFmpeg ${FF_FFMPEG_BASELINE} (${FF_BUILD_REVISION})"; then
rm -rf "${FFSRC}"; cp -a "${CACHE}/git/jellyfin-ffmpeg" "${FFSRC}"

# The jellyfin-ffmpeg tree is NOT pre-patched: its 95 changes live in
# debian/patches and are applied during its own packaging, which this build does
# not use. Configuring the checkout directly produces plain FFmpeg 7.1.4 — no
# tonemapx, no alphasrc, none of the fork's VAAPI/QSV/NVENC work — while every
# manifest still names the fork. Apply the series here, in series order, and
# prove afterwards that it took.
(
    cd "${FFSRC}"
    mapfile -t EXCLUDED < <(grep -vE '^\s*(#|$)' "${FF_EXCLUDED_PATCHES}" | awk '{print $1}')
    for e in "${EXCLUDED[@]}"; do
        [[ -f "debian/patches/${e}" ]] \
            || ff_die "excluded-patches.txt names ${e}, which is not in this series"
    done
    applied=0; skipped=0
    while read -r p; do
        [[ -n "${p}" ]] || continue
        for e in "${EXCLUDED[@]}"; do
            if [[ "${p}" == "${e}" ]]; then
                ff_log "skipping ${p} (declared unsafe)"; skipped=$((skipped + 1)); continue 2
            fi
        done
        # --forward and no fuzz: a patch that only applies approximately is a
        # patch applying to the wrong place. The series is pinned by the same
        # commit as the tree, so anything but a clean apply means the pin moved.
        patch -p1 --forward --no-backup-if-mismatch -F0 -i "debian/patches/${p}" \
            >> "${OUT}/patches.log" 2>&1 \
            || { tail -20 "${OUT}/patches.log" >&2; ff_die "patch ${p} did not apply cleanly"; }
        applied=$((applied + 1))
    done < debian/patches/series
    ff_log "applied ${applied} fork patches, skipped ${skipped}"

    # The series having run is not the same as the series having landed. Assert
    # the two things the distribution contract justifies the fork with.
    grep -q 'tonemapx' libavfilter/allfilters.c \
        || ff_die "the patch series applied but tonemapx is absent: the fork baseline did not land"
    grep -q 'alphasrc' libavfilter/allfilters.c \
        || ff_die "the patch series applied but alphasrc is absent: the fork baseline did not land"
    # And the one that must NOT have landed: FFmpeg's own nonfree classification
    # of libfdk_aac has to survive into the tree the build configures.
    awk '/^EXTERNAL_LIBRARY_NONFREE_LIST=/,/^"/' configure | grep -q 'libfdk_aac' \
        || ff_die "libfdk_aac is no longer in FFmpeg's nonfree list: 0029 was applied"
)
(
    cd "${FFSRC}"
    # Deliberately NOT ${CFLAGS}: FFmpeg records its configure line verbatim and
    # echoes it from -buildconf, so every -ffile-prefix-map argument would put
    # the path it maps away straight back into the shipped binary. FFmpeg builds
    # in-tree, so its __FILE__ values are already relative and it needs no
    # mapping; the dependencies, which cmake and meson build out of tree with
    # absolute paths, keep theirs.
    FF_CFLAGS="-O2 -g0 -fPIC -I${PREFIX}/include"
    ./configure \
        "${FLAGS[@]}" \
        --extra-version="${FF_BUILD_REVISION#*-}" \
        --pkg-config-flags=--static \
        --extra-cflags="${FF_CFLAGS}" \
        --extra-cxxflags="${FF_CFLAGS}" \
        --extra-ldflags="-Wl,--build-id=none -L${PREFIX}/lib -L${PREFIX}/lib64 ${RPATH_FLAG}" \
        --extra-libs="-lpthread -lm -ldl" \
        > "${OUT}/configure.log" 2>&1 || { tail -60 "${OUT}/configure.log" >&2; ff_die "FFmpeg configure failed"; }
    make -j"${J}" > "${OUT}/make.log" 2>&1 || { tail -80 "${OUT}/make.log" >&2; ff_die "FFmpeg build failed"; }
    make install DESTDIR="${WORK}" >> "${OUT}/make.log" 2>&1
)
    done_step "FFmpeg ${FF_FFMPEG_BASELINE} (${FF_BUILD_REVISION})"
fi

# =============================================================================
# stage the runtime
# =============================================================================
STAGE="${OUT}/tesserafin-ffmpeg-${FF_BUILD_REVISION}-${ARCH}"
rm -rf "${STAGE}"; mkdir -p "${STAGE}/bin" "${STAGE}/LICENSES"
FF_PREFIX="$(grep -oE '^--prefix=.*' "${FF_FLAGS_FILE}" | head -1 | cut -d= -f2-)"
install -m 0755 "${WORK}${FF_PREFIX}/bin/ffmpeg"  "${STAGE}/bin/ffmpeg"
install -m 0755 "${WORK}${FF_PREFIX}/bin/ffprobe" "${STAGE}/bin/ffprobe"
strip --strip-unneeded "${STAGE}/bin/ffmpeg" "${STAGE}/bin/ffprobe"

# The only shared libraries the runtime carries. Copied by real name with the
# soname symlink beside them, so the loader resolves them through $ORIGIN.
mkdir -p "${STAGE}/lib"
for soname in libva.so.2 libva-drm.so.2; do
    real="$(readlink -f "${PREFIX}/lib/${soname}")"
    install -m 0644 "${real}" "${STAGE}/lib/$(basename "${real}")"
    ln -sfn "$(basename "${real}")" "${STAGE}/lib/${soname}"
    strip --strip-unneeded "${STAGE}/lib/$(basename "${real}")"
done

patchelf --set-rpath '$ORIGIN/../lib' "${STAGE}/bin/ffmpeg" "${STAGE}/bin/ffprobe"
for b in "${STAGE}/bin/ffmpeg" "${STAGE}/bin/ffprobe"; do
    got="$(patchelf --print-rpath "${b}")"
    [[ "${got}" == '$ORIGIN/../lib' ]] \
        || ff_die "RUNPATH on $(basename "${b}") is '${got}', expected \$ORIGIN/../lib"
done

# No development headers, no static archives, no build caches: a runtime archive
# carries what the packages run and nothing else.
ff_log "staged $(du -sh "${STAGE}" | cut -f1) at ${STAGE}"
printf '%s\n' "${STAGE}"

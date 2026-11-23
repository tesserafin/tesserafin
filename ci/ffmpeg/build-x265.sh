#!/usr/bin/env bash
# Linux F0's static Main/Main10 library, from the pinned x265 source tree.
# Follows x265's build/linux/multilib.sh, limited to the supported 8/10-bit
# paths. CFLAGS/CXXFLAGS, SOURCE_DATE_EPOCH and the toolchain come from F0.
set -euo pipefail

SOURCE="${1:?source directory required}"
PREFIX="${2:?install prefix required}"
JOBS="${3:?fixed job count required}"
SOURCE="$(cd "${SOURCE}" && pwd)"

COMMON=( -DCMAKE_INSTALL_PREFIX="${PREFIX}" -DCMAKE_BUILD_TYPE=Release
         -DENABLE_SHARED=OFF -DENABLE_CLI=OFF -DENABLE_PIC=ON )

# Namespace the high-depth API so it can coexist with the public 8-bit API.
cmake -S "${SOURCE}/source" -B "${SOURCE}/build-10" "${COMMON[@]}" \
    -DHIGH_BIT_DEPTH=ON -DMAIN12=OFF -DEXPORT_C_API=OFF
cmake --build "${SOURCE}/build-10" -j"${JOBS}"

mkdir -p "${SOURCE}/build-8"
ln -sfn ../build-10/libx265.a "${SOURCE}/build-8/libx265_main10.a"
cmake -S "${SOURCE}/source" -B "${SOURCE}/build-8" "${COMMON[@]}" \
    -DHIGH_BIT_DEPTH=OFF -DEXPORT_C_API=ON \
    -DEXTRA_LIB=x265_main10.a -DEXTRA_LINK_FLAGS=-L. -DLINKED_10BIT=ON
cmake --build "${SOURCE}/build-8" -j"${JOBS}"

# A single archive lets FFmpeg's static link resolve both depth APIs. Retain
# duplicate member names: common.cpp.o, etc. occur in both namespaces. Extracting
# the archives into one directory would overwrite one depth with the other.
(
    cd "${SOURCE}/build-8"
    mv libx265.a libx265_main.a
    ar -M <<'MRI'
CREATE libx265.a
ADDLIB libx265_main.a
ADDLIB libx265_main10.a
SAVE
END
MRI
)
cmake --install "${SOURCE}/build-8"

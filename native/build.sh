#!/bin/sh
# Builds the native library: build/win/enginesim_native.dll for the game (mingw-w64),
# build/linux/ (enginesim_native.so + render_wav) for offline tests.
set -e
cd "$(dirname "$0")"
cmake -S . -B build/win -DCMAKE_TOOLCHAIN_FILE=mingw-w64.cmake -DCMAKE_BUILD_TYPE=Release >/dev/null
cmake --build build/win -j"$(nproc)" --target enginesim
cmake -S . -B build/linux -DCMAKE_BUILD_TYPE=Release >/dev/null
cmake --build build/linux -j"$(nproc)"

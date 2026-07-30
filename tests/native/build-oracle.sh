#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repository_dir=$(CDPATH= cd -- "$script_dir/../.." && pwd)
sameboy_dir=${SAMEBOY_SOURCE_DIR:-"$repository_dir/../SameBoy"}
expected_commit=213a12ce93d66b105a113debd9396306066a7cfc
output_dir="$script_dir/build"

if [ ! -f "$sameboy_dir/Core/gb.c" ]; then
    echo "SameBoy source was not found at $sameboy_dir" >&2
    exit 1
fi

if command -v git >/dev/null 2>&1 && [ -d "$sameboy_dir/.git" ]; then
    actual_commit=$(git -C "$sameboy_dir" rev-parse HEAD)
    if [ "$actual_commit" != "$expected_commit" ]; then
        echo "SameBoy must be pinned to $expected_commit (found $actual_commit)" >&2
        exit 1
    fi
fi

CCACHE_DISABLE=1 make -s -C "$sameboy_dir" lib CONF=debug \
    DISABLE_TIMEKEEPING=1 DISABLE_REWIND=1 DISABLE_DEBUGGER=1 \
    DISABLE_CHEATS=1

mkdir -p "$output_dir"
cc -std=c11 -fPIC -fvisibility=hidden -I"$sameboy_dir" \
    "$script_dir/oracle.c" -shared -L"$sameboy_dir/build/lib" -lsameboy \
    -Wl,-rpath,'$ORIGIN' -o "$output_dir/libcraterboy-oracle.so"
cp "$sameboy_dir/build/lib/libsameboy.so" "$output_dir/libsameboy.so"

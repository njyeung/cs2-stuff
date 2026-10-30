#!/usr/bin/env bash
# Builds theatrevoice.so with the flags Metamod:Source's AMBuild uses for CS2 plugins.
# Run inside the Steam Runtime sniper SDK, so the binary matches the game's runtime:
#
#   docker run --rm -v "$PWD":/src -w /src registry.gitlab.steamos.cloud/steamrt/sniper/sdk \
#     theatre-voice/build.sh <hl2sdk-cs2 dir> <metamod-source dir> <output dir>
#
# The output directory receives theatrevoice/bin/theatrevoice.so and metamod/theatrevoice.vdf,
# laid out to be copied into the server's addons directory.
set -euo pipefail

sdk=$(realpath "$1")
mms=$(realpath "$2")
out=$(realpath -m "$3")
here=$(dirname "$(realpath "$0")")

gen=$(mktemp -d)
trap 'rm -rf "$gen"' EXIT
mkdir -p "$out/theatrevoice/bin" "$out/metamod"

# The SDK headers include protobuf headers generated from its .proto files.
"$sdk/devtools/bin/linux/protoc" --cpp_out="$gen" -I"$sdk/common" \
	-I"$sdk/thirdparty/protobuf-3.21.8/src" "$sdk/common/network_connection.proto"

# interfaces.a stays visible: it provides CreateInterface, Metamod's entry point.
# memoverride.cpp routes this module's allocations through tier0, as the game's are,
# so memory protobuf allocates here can safely be freed by the game and vice versa.
# Sniper's default clang 11 can't parse its libstdc++ 10 C++20 headers; GCC 14 can.
"${CXX:-g++-14}" -std=c++20 -O2 -fPIC -shared -fvisibility=hidden -fvisibility-inlines-hidden \
	-fno-strict-aliasing -msse -fno-omit-frame-pointer \
	-Wall -Wno-non-virtual-dtor -Wno-overloaded-virtual -Wno-unknown-pragmas -Wno-sign-compare \
	-Wno-unused -Wno-switch -Wno-dangling-else \
	-DNDEBUG -DLINUX -D_LINUX -DPOSIX -DGNUC -DCOMPILER_GCC -DX64BITS -DPLATFORM_64BITS \
	-D_FILE_OFFSET_BITS=64 -DHAVE_STDINT_H -D_GLIBCXX_USE_CXX11_ABI=0 \
	-Dstricmp=strcasecmp -D_stricmp=strcasecmp -D_snprintf=snprintf -D_vsnprintf=vsnprintf \
	-DMETA_IS_SOURCE2 -DSOURCE_ENGINE=25 -DSE_CS2=25 -DGAME_DLL -DRAD_TELEMETRY_DISABLED \
	-I"$mms/core" -I"$mms/third_party/khook/include" -I"$mms/third_party/amtl" \
	-I"$sdk/thirdparty/protobuf-3.21.8/src" -I"$sdk/public" -I"$sdk/public/engine2" \
	-I"$sdk/public/mathlib" -I"$sdk/public/tier0" -I"$sdk/public/tier1" -I"$sdk/public/entity2" \
	-I"$sdk/game/shared" -I"$sdk/common" -I"$gen" \
	"$here/theatre_voice.cpp" "$sdk/public/tier0/memoverride.cpp" \
	"$sdk/lib/linux64/interfaces.a" "$sdk/lib/linux64/release/libprotobuf.a" \
	"$sdk/lib/linux64/libtier0.so" \
	-static-libstdc++ -static-libgcc -Wl,--no-undefined \
	-Wl,--exclude-libs,libprotobuf.a:libstdc++.a:libsupc++.a:libgcc.a:libgcc_eh.a \
	-o "$out/theatrevoice/bin/theatrevoice.so"

cp "$here/theatrevoice.vdf" "$out/metamod/theatrevoice.vdf"
echo "built $out/theatrevoice/bin/theatrevoice.so"

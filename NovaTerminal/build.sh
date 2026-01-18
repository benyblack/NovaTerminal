#!/bin/bash
set -e

echo "Building Native Rust Library..."
cd native
export CARGO_TARGET_DIR=target_unix
export CARGO_INCREMENTAL=0
cargo build --release
cd ..

echo "Publishing .NET Application..."
rm -rf bin obj
dotnet publish -c Release -r linux-x64 --self-contained false -o bin/Dist

echo "Running NovaTerminal..."
export LD_PRELOAD="$PWD/bin/Dist/libSkiaSharp.so"
./bin/Dist/NovaTerminal

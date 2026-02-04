#!/bin/bash
set -e

echo "Building Native Rust Library..."

# WSL/Linux on a Windows drive (e.g. /mnt/d) can cause Cargo metadata errors. 
# We'll build in a local Linux directory and copy the result back.
BUILD_DIR="/tmp/nova_terminal_native_build"
mkdir -p "$BUILD_DIR"

cd native
cargo build --release --target-dir "$BUILD_DIR"
cd ..

# Ensure the target directory exists in our workspace for the .NET build
mkdir -p native/target_linux/release
cp "$BUILD_DIR/release/librusty_pty.so" native/target_linux/release/

echo "Building .NET Application..."
dotnet build

# Force the correct SkiaSharp library to be in the execution path
# This solves the "version mismatch" when WSL picks up old system libraries
SKIA_DLL_PATH=$(find bin -name "libSkiaSharp.so" | grep "linux-x64" | head -n 1)
if [ -f "$SKIA_DLL_PATH" ]; then
    echo "Found SkiaSharp native library at $SKIA_DLL_PATH. Copying to output root..."
    cp "$SKIA_DLL_PATH" bin/Debug/net10.0/
fi

echo "------------------------------------------------"
echo "Build Complete!"
echo "To run the application on Linux, use:"
echo "LD_LIBRARY_PATH=bin/Debug/net10.0/ dotnet run"
echo "------------------------------------------------"

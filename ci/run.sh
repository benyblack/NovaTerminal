#!/usr/bin/env bash
set -e

echo "=== CLEAN ==="
dotnet clean

echo "=== RESTORE ==="
dotnet restore

echo "=== BUILD RELEASE ==="
dotnet build -c Release -warnaserror

echo "=== TEST ==="
dotnet test -c Release --no-build

echo "=== REPLAY TESTS ==="
dotnet test -c Release --filter Category=Replay

echo "=== FORMAT CHECK ==="
dotnet format --verify-no-changes

echo "CI SUCCESS"
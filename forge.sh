#!/usr/bin/env bash
# forge.sh - convenience wrapper for the Forge compiler on Linux/macOS.
# Usage:
#   ./forge.sh build examples/hello.fg                 -> writes hello.exe (Windows target, default)
#   ./forge.sh run   examples/hello.fg                 -> builds then runs it (via Wine if installed)
#   ./forge.sh build examples/hello.fg --target linux  -> writes a Linux ELF binary instead
#
# Requires the .NET 10 SDK on PATH (the `dotnet` command).
set -e

DLL="bin/Release/net10.0/forgec.dll"
if [ ! -f "$DLL" ]; then
    echo "Compiler not built yet. Building it now..."
    dotnet build -c Release
fi

exec dotnet "$DLL" "$@"

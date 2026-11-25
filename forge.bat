@echo off
REM forge.bat - convenience wrapper for the Forge compiler on Windows.
REM Usage:
REM   forge build examples\hello.fg            -> writes hello.exe
REM   forge run   examples\hello.fg            -> builds then runs it
REM   forge build examples\hello.fg -o out.exe -> custom output name
REM
REM Works from any folder (paths are resolved relative to this script).
REM Requires the .NET 10 SDK on PATH (the `dotnet` command).

set "FORGE_DIR=%~dp0"
if not exist "%FORGE_DIR%bin\Release\net10.0\forgec.dll" (
    echo Compiler not built yet. Building it now...
    dotnet build -c Release "%FORGE_DIR%ForgeC.csproj"
)

dotnet "%FORGE_DIR%bin\Release\net10.0\forgec.dll" %*

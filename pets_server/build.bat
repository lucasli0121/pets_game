@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

REM ============================================================
REM  pets_server build script
REM
REM  Usage: double-click build.bat, or run it from cmd
REM  Output: .deploy\pets_server_deploy.tar.gz
REM
REM  All messages are ASCII on purpose: Chinese text in .bat files
REM  breaks under cmd's codepage handling and can be parsed as
REM  commands (seen: "is not recognized as an internal command").
REM ============================================================

cd /d "%~dp0"

echo ============================================
echo  pets_server build
echo ============================================
echo.

REM ---------- 1. check Go ----------
echo [1/4] Check Go toolchain
go version >nul 2>&1
if errorlevel 1 (
    echo       [FAIL] go not found in PATH
    goto :fail
)
for /f "tokens=3" %%v in ('go version') do echo       Go: %%v

REM ---------- 2. cross compile ----------
echo.
echo [2/4] Cross-compile for Linux amd64
set GOOS=linux
set GOARCH=amd64
set CGO_ENABLED=0
set GO111MODULE=on

if not exist bin mkdir bin
go build -trimpath -ldflags="-s -w" -o bin\pets_server_linux .\cmd\server
if errorlevel 1 (
    echo       [FAIL] build failed, see Go output above
    goto :fail
)
if not exist bin\pets_server_linux (
    echo       [FAIL] build finished but no output file
    goto :fail
)
for %%F in (bin\pets_server_linux) do echo       output: %%F  [%%~zF bytes]

REM ---------- 3. pack ----------
echo.
echo [3/4] Pack deploy bundle
if not exist .deploy mkdir .deploy

REM Required files check. A missing one makes tar print "Couldn't visit directory"
REM with an empty filename, which tells you nothing. Name them explicitly instead.
REM NOTE: never put non-ASCII in this file, not even in REM lines -- cmd's
REM codepage handling truncates the byte stream and the tail gets executed
REM as a command (seen: "'is not recognized as an internal command").
for %%F in (.deploy\pets_server.service .deploy\README-deploy.md cfg\pets_server.yaml) do (
    if not exist "%%F" (
        echo       [FAIL] missing: %%F
        echo       restore it before packing
        goto :fail
    )
)

if exist .deploy\pets_server_deploy.tar.gz del /q .deploy\pets_server_deploy.tar.gz

REM Forward slashes are mandatory here. Windows tar preserves backslashes
REM inside the archive, producing entries like "bin\\pets_server_linux".
REM On Linux that extracts as one filename containing a backslash instead of
REM a bin/ directory -- the binary ends up in the wrong place.
REM cfg/ carries pets_server.yaml. The server reads it from <workdir>/cfg/,
REM so the directory layout has to survive packing: extract, then start the
REM binary from the bundle root. Starting from inside bin/ loses the config
REM silently and falls back to hardcoded defaults.
tar -czf .deploy/pets_server_deploy.tar.gz bin/pets_server_linux assets cfg .deploy/pets_server.service .deploy/README-deploy.md
if errorlevel 1 (
    echo       [FAIL] pack failed
    echo       if tar.exe is missing, run this from Git Bash instead:
    echo       tar -czf .deploy/pets_server_deploy.tar.gz bin/pets_server_linux assets .deploy/
    goto :fail
)

REM ---------- 4. done ----------
echo.
echo [4/4] Done
for %%F in (.deploy\pets_server_deploy.tar.gz) do echo       package: %%F  [%%~zF bytes]
echo.
echo ============================================
echo  Next steps
echo ============================================
echo   1. upload : scp .deploy\pets_server_deploy.tar.gz root@your-server:/tmp/
echo   2. unpack : tar -xzf /tmp/pets_server_deploy.tar.gz -C ^<target-dir^>
echo   3. run    : cd ^<target-dir^> ^&^& chmod +x bin/pets_server_linux ^&^& ./bin/pets_server_linux
echo.
echo   The bundle only contains the binary, assets and a sample systemd unit.
echo   Deployment layout, service registration and Nginx are up to you.
echo   Set MONGO_URI env var before running, e.g.:
echo     MONGO_URI="mongodb://pets_game:pets_game@127.0.0.1:27017/?authSource=pets_game"
echo.
echo   NOTE: this binary is Linux-only. For local debugging use bin\pets_server.exe
echo.
endlocal
exit /b 0

:fail
echo.
echo BUILD ABORTED
endlocal
exit /b 1

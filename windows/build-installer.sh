#!/bin/zsh
# Builds the Windows installer from macOS (or Linux): dist/AFAN-Traders-Setup-<version>.exe
set -e
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
rm -rf dist/publish
dotnet publish AfanTraders.App/AfanTraders.App.csproj -c Release -o dist/publish
makensis -V2 installer/afan-traders.nsi
ls -la dist/*.exe

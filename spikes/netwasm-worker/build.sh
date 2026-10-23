#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

cd "$root/csharp"
dotnet publish -c Release

rm -rf "$root/public/browser"
cp -R bin/Release/netwasm0.1/publish/browser "$root/public/browser"

cd "$root"
python3 patch-host.py
echo "Built $root/public/browser"

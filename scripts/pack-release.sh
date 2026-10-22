#!/usr/bin/env bash
# Build the Snip PowerToys Run plugin (Release) and zip slim + full packages.
# Works on Windows (Git Bash) and on Linux with -p:EnableWindowsTargeting=true.
#
# Outputs (under artifacts/):
#   Snip-<ver>-slim.zip  — no wco_data (~61 MB saved); OCR downloads on first use
#   Snip-<ver>-full.zip  — includes wco_data (offline OCR)
#   Snip-<ver>.zip       — alias of slim (default / recommended)
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
# Default matches a typical Windows PowerToys install. On Linux CI / cross-build,
# pass an explicit dir that contains Wox.Plugin.dll (stubs are fine for packaging).
POWERTOYS_INSTALL_DIR="${POWERTOYS_INSTALL_DIR:-C:/Program Files/PowerToys}"
if [[ "${1:-}" == "--powertoys-dir" && -n "${2:-}" ]]; then
  POWERTOYS_INSTALL_DIR="$2"
  shift 2
elif [[ -n "${1:-}" && "${1}" != -* ]]; then
  POWERTOYS_INSTALL_DIR="$1"
  shift
fi

OUTPUT_DIR="${OUTPUT_DIR:-$REPO_ROOT/artifacts}"
PROJECT="$REPO_ROOT/Community.PowerToys.Run.Plugin.Snip/Community.PowerToys.Run.Plugin.Snip.csproj"
PLUGIN_JSON="$REPO_ROOT/Community.PowerToys.Run.Plugin.Snip/plugin.json"

if [[ ! -f "$PROJECT" ]]; then
  echo "Project not found: $PROJECT" >&2
  exit 1
fi

VERSION="$(python3 -c "import json; print(json.load(open(r'''$PLUGIN_JSON'''))['Version'])")"
echo "==> Building $CONFIGURATION (version $VERSION)"
echo "    PowerToysInstallDir=$POWERTOYS_INSTALL_DIR"

dotnet build "$PROJECT" \
  -c "$CONFIGURATION" \
  -p:EnableWindowsTargeting=true \
  -p:PowerToysInstallDir="$POWERTOYS_INSTALL_DIR" \
  -v:m

BUILD_OUT="$REPO_ROOT/Community.PowerToys.Run.Plugin.Snip/bin/$CONFIGURATION"
if [[ ! -d "$BUILD_OUT" ]]; then
  echo "Build output not found: $BUILD_OUT" >&2
  exit 1
fi

STAGE_ROOT="$OUTPUT_DIR/staging"
STAGE_PLUGIN="$STAGE_ROOT/Snip"
rm -rf "$STAGE_ROOT"
mkdir -p "$STAGE_PLUGIN"

echo "==> Staging plugin files from $BUILD_OUT"
# Copy tree, skip pdb
if command -v rsync >/dev/null 2>&1; then
  rsync -a --exclude='*.pdb' "$BUILD_OUT"/ "$STAGE_PLUGIN"/
else
  cp -a "$BUILD_OUT"/. "$STAGE_PLUGIN"/
  find "$STAGE_PLUGIN" -name '*.pdb' -type f -delete
fi

assert_exists() {
  local rel="$1"
  if [[ ! -e "$STAGE_PLUGIN/$rel" ]]; then
    echo "Self-check failed: missing $rel" >&2
    exit 1
  fi
  echo "  OK  $rel"
}

echo "==> Self-check (common)"
assert_exists "plugin.json"
assert_exists "Community.PowerToys.Run.Plugin.Snip.dll"
assert_exists "Images/Snip.dark.png"
assert_exists "Images/Snip.light.png"
assert_exists "WeChatOcr.dll"
assert_exists "Sdcb.HyMT2Sharp.dll"
assert_exists "zh-CN/Community.PowerToys.Run.Plugin.Snip.resources.dll"

# Build output still contains wco_data (NuGet content); full zip keeps it, slim strips it.
assert_exists "wco_data"
assert_exists "wco_data/WeChatOCR.exe"
WCO_COUNT="$(find "$STAGE_PLUGIN/wco_data" -type f | wc -l | tr -d ' ')"
if [[ "$WCO_COUNT" -lt 3 ]]; then
  echo "Self-check failed: wco_data looks incomplete ($WCO_COUNT files)" >&2
  exit 1
fi
echo "  OK  wco_data ($WCO_COUNT files) — will ship in full zip only"

python3 - "$STAGE_PLUGIN/plugin.json" "$VERSION" <<'PY'
import json, sys
path, expected = sys.argv[1], sys.argv[2]
data = json.load(open(path, encoding="utf-8"))
author = data.get("Author") or ""
website = data.get("Website") or ""
version = data.get("Version") or ""
if author == "Microsoft Corporation" or "aka.ms" in website:
    raise SystemExit("Self-check failed: plugin.json still has Microsoft / aka.ms metadata")
if version != expected:
    raise SystemExit(f"Self-check failed: staged Version {version!r} != {expected!r}")
print(f"  OK  plugin.json Author={author} Version={version}")
PY

mkdir -p "$OUTPUT_DIR"

zip_plugin() {
  local zip_path="$1"
  rm -f "$zip_path"
  (
    cd "$STAGE_ROOT"
    if command -v zip >/dev/null 2>&1; then
      zip -qr "$zip_path" Snip
    else
      python3 - "$zip_path" <<'PY'
import sys, zipfile
from pathlib import Path
zip_path = Path(sys.argv[1])
root = Path("Snip")
with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
    for p in root.rglob("*"):
        if p.is_file():
            zf.write(p, p.as_posix())
print("zipped via python")
PY
    fi
  )
  local size
  size="$(python3 -c "import os; print(round(os.path.getsize(r'''$zip_path''')/1024/1024, 1))")"
  echo "  -> $zip_path (${size} MB)"
}

FULL_ZIP="$OUTPUT_DIR/Snip-$VERSION-full.zip"
SLIM_ZIP="$OUTPUT_DIR/Snip-$VERSION-slim.zip"
DEFAULT_ZIP="$OUTPUT_DIR/Snip-$VERSION.zip"

echo "==> Zipping FULL (with wco_data)"
zip_plugin "$FULL_ZIP"

echo "==> Stripping wco_data for SLIM package"
rm -rf "$STAGE_PLUGIN/wco_data"
if [[ -e "$STAGE_PLUGIN/wco_data" ]]; then
  echo "Failed to remove wco_data for slim package" >&2
  exit 1
fi
echo "  OK  wco_data removed from staging"

echo "==> Zipping SLIM (no wco_data; OCR on-demand)"
zip_plugin "$SLIM_ZIP"

# Default zip name = slim (recommended for most users).
cp -f "$SLIM_ZIP" "$DEFAULT_ZIP"
echo "  -> $DEFAULT_ZIP (copy of slim)"

echo "==> Done"
echo "  slim (default): $SLIM_ZIP  /  $DEFAULT_ZIP"
echo "  full (offline OCR): $FULL_ZIP"
echo "Unzip into: %LOCALAPPDATA%\\Microsoft\\PowerToys\\PowerToys Run\\Plugins\\"
echo "Then restart PowerToys. Folder name inside the zip is Snip\\."

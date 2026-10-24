#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Cities_Skylines/Cities.app/Contents/Resources/Data/Managed"
MOD_NAME="MorePropsBETA"
MOD_DIR="$HOME/Library/Application Support/Colossal Order/Cities_Skylines/Addons/Mods/$MOD_NAME"
HARMONY_DLL="$SCRIPT_DIR/Libs/CitiesHarmony.Harmony.dll"
CITIES_HARMONY_API_DLL="$SCRIPT_DIR/Libs/CitiesHarmony.API.dll"

echo "=========================================================="
echo "🚀 Building $MOD_NAME (by NgocDuy)..."
echo "=========================================================="

# 1. Compile MorePropsBETA.dll with mcs
echo "1️⃣ Compiling $MOD_NAME.dll with mcs..."
mcs -sdk:2 -target:library \
    -r:"$GAME_DIR/Assembly-CSharp.dll" \
    -r:"$GAME_DIR/ICities.dll" \
    -r:"$GAME_DIR/ColossalManaged.dll" \
    -r:"$GAME_DIR/UnityEngine.dll" \
    -r:"$HARMONY_DLL" \
    -r:"$CITIES_HARMONY_API_DLL" \
    -out:"$MOD_NAME.dll" \
    src/*.cs

echo "✅ Compiled $MOD_NAME.dll successfully."

# 2. Deploy to game local mods directory
echo "2️⃣ Deploying to Cities: Skylines Addons/Mods/$MOD_NAME..."
mkdir -p "$MOD_DIR"
cp "$MOD_NAME.dll" "$MOD_DIR/"
cp "$CITIES_HARMONY_API_DLL" "$MOD_DIR/"
[ -f preview-image.png ] && cp preview-image.png "$MOD_DIR/"

# 3. Deploy to SteamCMD content staging
echo "3️⃣ Deploying to SteamCMD content staging ($SCRIPT_DIR/content)..."
mkdir -p "$SCRIPT_DIR/content"
cp "$MOD_NAME.dll" "$SCRIPT_DIR/content/"
cp "$CITIES_HARMONY_API_DLL" "$SCRIPT_DIR/content/"

WORKSHOP_DIR="$HOME/Library/Application Support/Steam/steamapps/workshop/content/255710/3826164167"
echo "4️⃣ Deploying to Steam Workshop directory ($WORKSHOP_DIR)..."
mkdir -p "$WORKSHOP_DIR"
cp "$MOD_NAME.dll" "$WORKSHOP_DIR/"
cp "$CITIES_HARMONY_API_DLL" "$WORKSHOP_DIR/"
[ -f preview-image.png ] && cp preview-image.png "$WORKSHOP_DIR/PreviewImage.png"

echo "=========================================================="
echo "🎉 DEPLOYMENT COMPLETE!"
echo "Files in $MOD_DIR:"
ls -lh "$MOD_DIR"
echo "Files in $SCRIPT_DIR/content:"
ls -lh "$SCRIPT_DIR/content"
echo "=========================================================="

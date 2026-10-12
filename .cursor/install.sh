#!/usr/bin/env bash
# Cloud agent setup for the desktop status board.
# Installs the Python packages in requirements.txt, DejaVu fonts for the
# wallpaper renderer, and PowerShell 7 so verify.py can emit task XML on Linux.
set -euo pipefail

export DEBIAN_FRONTEND=noninteractive

need_apt=0
if ! command -v python3 >/dev/null 2>&1; then
  need_apt=1
fi
if ! python3 -m pip --version >/dev/null 2>&1; then
  need_apt=1
fi
if [ ! -f /usr/share/fonts/truetype/dejavu/DejaVuSans.ttf ]; then
  need_apt=1
fi
if [ "$need_apt" -eq 1 ]; then
  sudo apt-get update -qq
  sudo apt-get install -y python3 python3-pip fonts-dejavu-core
fi

if ! command -v pwsh >/dev/null 2>&1; then
  arch="$(dpkg --print-architecture)"
  case "$arch" in
    amd64|arm64) ;;
    *) echo "unsupported architecture for PowerShell: $arch" >&2; exit 1 ;;
  esac
  deb="$(mktemp)"
  curl -fsSL -o "$deb" \
    "https://github.com/PowerShell/PowerShell/releases/download/v7.4.6/powershell_7.4.6-1.deb_${arch}.deb"
  sudo apt-get update -qq
  sudo apt-get install -y libicu74 libunwind8 || true
  sudo dpkg -i "$deb" || sudo apt-get install -y -f
  rm -f "$deb"
  command -v pwsh >/dev/null 2>&1
fi

python3 -m pip install --user -r requirements.txt \
  || python3 -m pip install --user --break-system-packages -r requirements.txt

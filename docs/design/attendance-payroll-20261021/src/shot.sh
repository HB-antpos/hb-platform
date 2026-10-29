#!/bin/bash
# 用法：shot.sh 名称 宽度 —— 先量内容高度，再按 2x 截图到上一级目录（设计图 PNG 所在处）
set -e
cd "$(dirname "$0")"
C="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
H=$("$C" --headless=new --disable-gpu --hide-scrollbars --window-size="$2",1200 --virtual-time-budget=8000 --dump-dom "file://$PWD/$1.html" 2>/dev/null | grep -o 'data-h="[0-9]*"' | grep -o '[0-9]*')
"$C" --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=2 --window-size="$2","$H" --virtual-time-budget=8000 --screenshot="$PWD/../$1.png" "file://$PWD/$1.html" 2>/dev/null
echo "$1.png ${2}x${H}"

#!/usr/bin/env bash
# PhotoGrader 单文件发布
#
# 产出：src/PhotoGrader.App/bin/Release/net8.0-windows/win-x64/publish/PhotoGrader.App.exe
#
# 采用「依赖 .NET 运行时」的方式，因此 exe 只有 10 MB 出头。
# 代价是目标电脑需要先装 .NET 8 桌面运行时（Desktop Runtime，不是 SDK）：
#   https://dotnet.microsoft.com/download/dotnet/8.0
# 若想换成免安装的自包含版本，把下面的 --self-contained false 改成 true，
# exe 会涨到 150 MB 左右，但拷到任何 Windows 上双击就能跑。
#
# 前端资源（wwwroot）已内嵌进程序集，发布目录里只会有一颗 exe，不会散落文件夹。
#
# 用法：./publish.sh

set -euo pipefail

cd "$(dirname "$0")" || exit 1

OUT_DIR="src/PhotoGrader.App/bin/Release/net8.0-windows/win-x64/publish"

./dn.sh publish src/PhotoGrader.App \
  -c Release \
  -r win-x64 \
  --self-contained false \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none

echo
echo "=== 发布产物 ==="
ls -la "$OUT_DIR"

COUNT=$(ls -1 "$OUT_DIR" | wc -l | tr -d ' ')
SIZE=$(du -h "$OUT_DIR/PhotoGrader.App.exe" | cut -f1)

echo
if [ "$COUNT" = "1" ]; then
  echo "✓ 单文件为 $SIZE，目录内无其他文件"
else
  echo "⚠ 目录内有 $COUNT 个文件（预期 1 个），请检查："
  ls -1 "$OUT_DIR"
fi

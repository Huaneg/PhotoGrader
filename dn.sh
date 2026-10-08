#!/usr/bin/env bash
# PhotoGrader dotnet wrapper
#
# 背景：Git Bash 会话里有时缺少若干标准 Windows 环境变量
# （ProgramFiles / ProgramData / APPDATA / COMSPEC），
# dotnet CLI 在执行 NuGet 相关操作时会因内部路径拼接拿到 null
# 而抛出 "Value cannot be null. (Parameter 'path1')"。
# 此处补齐后再调用 dotnet。
#
# 已在环境里的变量不会被覆盖（用 :- 兜底）。
#
# 用法：./dn.sh build   /   ./dn.sh restore   /   ./dn.sh test

# 从用户主目录推导，避免把具体用户名写死进脚本
PROFILE_WIN="${USERPROFILE:-}"
if [ -z "$PROFILE_WIN" ]; then
  PROFILE_WIN=$(cygpath -w "$HOME" 2>/dev/null || echo "$HOME")
fi

export APPDATA="${APPDATA:-$PROFILE_WIN\\AppData\\Roaming}"
export LOCALAPPDATA="${LOCALAPPDATA:-$PROFILE_WIN\\AppData\\Local}"
export ProgramData="${ProgramData:-C:\\ProgramData}"
export ProgramFiles="${ProgramFiles:-C:\\Program Files}"
export PROGRAMW6432="${PROGRAMW6432:-C:\\Program Files}"
export COMSPEC="${COMSPEC:-C:\\Windows\\System32\\cmd.exe}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$PROFILE_WIN\\.nuget\\packages}"

cd "$(dirname "$0")" || exit 1
exec dotnet "$@"

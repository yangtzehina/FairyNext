#!/usr/bin/env bash
# FairyNext oracle 截取器 —— 驱动钉死的 fork（oracle）产出一份 golden 三件套。
#
#   ./tools/oracle/capture.sh scene-001                 # 写入 tests/goldens/oracle/scene-001/
#   ./tools/oracle/capture.sh scene-001 --out /tmp/x    # 写到别处（重生成前先比对用）
#   ./tools/oracle/capture.sh --check                   # 只做环境与 SHA 体检，不动编辑器
#
# 纪律：本脚本对 oracle 仓库**只读**。它开的是 untitled 空场景（Scene.New empty），跑完把原场景开回来，
# 全部产物写到 FairyNext 侧。除 Unity 自己的 Library/ Temp/ Logs/（均已 gitignore）外不落任何文件到 oracle。
# 需要真的改 oracle 时走 README 的 bump SHA 流程，不是在这里加写操作。
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ORACLE_DIR="${ORACLE_DIR:-$HOME/ECS/FairyGUI-unity}"
UNICLI="${UNICLI:-$HOME/bin/unicli}"
DECLARATIONS="$REPO_ROOT/tools/oracle/lib/declarations.cs"
LOCK="$REPO_ROOT/oracle.lock"
TIMEOUT_MS="${ORACLE_TIMEOUT_MS:-180000}"
UC_OUT=""        # 最近一次 ucok 的响应体

die() { echo "oracle: $*" >&2; exit 1; }
note() { echo "oracle: $*"; }

# oracle.lock 是唯一事实源；这里只读它，不复制它的值。
lock_field() { sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" "$LOCK" | head -1; }

preflight() {
  [ -x "$UNICLI" ] || die "找不到 unicli（$UNICLI）。设 UNICLI=/path/to/unicli 或装 UniCli。"
  [ -d "$ORACLE_DIR/Assets" ] || die "oracle 工程不在 $ORACLE_DIR。设 ORACLE_DIR 指过去。"
  [ -f "$LOCK" ] || die "缺 oracle.lock"

  LOCK_SHA="$(lock_field sha)"
  LOCK_UNITY="$(lock_field unityVersion)"
  [ -n "$LOCK_SHA" ] || die "oracle.lock 里读不到 sha"

  HEAD_SHA="$(git -C "$ORACLE_DIR" rev-parse --short=7 HEAD)"
  case "$HEAD_SHA" in
    "$LOCK_SHA"*) : ;;
    *) die "oracle HEAD=$HEAD_SHA ≠ oracle.lock sha=$LOCK_SHA。基线只在钉死的 SHA 上生成；要换 SHA 走 README 的 bump 流程。" ;;
  esac

  PROJ_UNITY="$(tr -d '\r' < "$ORACLE_DIR/ProjectSettings/ProjectVersion.txt" | sed -n 's/^m_EditorVersion: //p')"
  [ "$PROJ_UNITY" = "$LOCK_UNITY" ] || die "oracle 工程 Unity=$PROJ_UNITY ≠ oracle.lock=$LOCK_UNITY"

  export UNICLI_PROJECT="$ORACLE_DIR"
  "$UNICLI" check >/dev/null 2>&1 || die "Unity 编辑器服务器没连上。先打开 $ORACLE_DIR（Unity $LOCK_UNITY），再重跑。"
  DRIVER_VERSION="unicli $("$UNICLI" --version 2>/dev/null | tr -d '\n')"

  note "oracle=$ORACLE_DIR @ $HEAD_SHA · Unity $PROJ_UNITY · $DRIVER_VERSION"
  wait_idle
}

# 编译或资产导入中途发的命令会碰上域重载，服务器连接在中间断开——先等空闲再开工。
wait_idle() {
  local i out
  for i in $(seq 1 120); do
    out="$(uc exec Editor.Status 2>/dev/null || true)"
    if echo "$out" | grep -q '"isCompiling": false' && echo "$out" | grep -q '"isUpdating": false'; then
      return 0
    fi
    sleep 1
  done
  die "等编辑器空闲（isCompiling/isUpdating=false）超时"
}

# unicli exec/eval 的 --json 输出里 success 字段就是判定位。
uc() { "$UNICLI" "$@" --json --no-focus --timeout "$TIMEOUT_MS"; }

# ucok：跑一条命令并要求 success=true，成功时把响应留在 $UC_OUT。
# 「JSON parse error」是服务器把命名管道里的两份 JSON 当成一份时的报错，与参数内容无关，
# 实测在人工操作编辑器之后偶发（约 1/5）。这类失败意味着命令**根本没执行**，因此重试一次是安全的；
# 其余失败一律立即 die，不掩盖。
ucok() {
  local out="" i
  for i in 1 2; do
    out="$(uc "$@" || true)"
    if echo "$out" | grep -q '"success": true'; then UC_OUT="$out"; return 0; fi
    echo "$out" | grep -q "JSON parse error" || break
    note "UniCli 协议层偶发，重试：$*"
    sleep 2
  done
  echo "$out" >&2
  die "$* 失败"
}

wait_play() {   # wait_play true|false
  local want="$1" i out
  for i in $(seq 1 60); do
    out="$(uc exec PlayMode.Status 2>/dev/null || true)"
    echo "$out" | grep -q "\"isPlaying\": $want" && return 0
    sleep 1
  done
  die "等 PlayMode isPlaying=$want 超时"
}

capture() {
  local scene_id="$1" out_dir="$2"
  local scene_file="$REPO_ROOT/tools/oracle/scenes/$scene_id.json"
  [ -f "$scene_file" ] || die "场景描述符不存在：$scene_file"
  mkdir -p "$out_dir"

  # 进来时若还在 Play Mode（上一次跑挂了），先退出：否则 Scene.New 拿到的是临时播放场景，
  # 「跑完还原原场景」这条会把用户的编辑态还原成错的东西。
  ucok exec PlayMode.Status
  if echo "$UC_OUT" | grep -q '"isPlaying": true'; then
    note "编辑器仍在 Play Mode，先退出"
    ucok exec PlayMode.Exit
    wait_play false
  fi

  local prev_scene
  ucok exec Scene.GetActive
  prev_scene="$(echo "$UC_OUT" | sed -n 's/.*"path": "\([^"]*\)".*/\1/p' | head -1)"
  note "当前场景=${prev_scene:-<untitled>}，跑完还原"

  ucok exec Scene.New '{"empty":true,"dirtyAction":"discard"}'
  ucok exec PlayMode.Enter
  wait_play true

  ucok eval "return FairyNextOracle.Run(@\"$scene_file\", @\"$out_dir\", @\"$HEAD_SHA\", @\"$DRIVER_VERSION\");" \
       --declarations "$(cat "$DECLARATIONS")"
  local payload
  payload="$(echo "$UC_OUT" | sed -n 's/.*"result": "\(.*\)",/\1/p' | head -1)"

  ucok exec PlayMode.Exit
  wait_play false
  if [ -n "$prev_scene" ]; then
    ucok exec Scene.Open "{\"path\":\"$prev_scene\",\"dirtyAction\":\"discard\"}"
  fi

  case "$payload" in
    OK*) : ;;
    *) die "驱动返回：$payload" ;;
  esac
  for f in frame.png layout.json meta.json; do
    [ -s "$out_dir/$f" ] || die "产物缺失或为空：$out_dir/$f"
  done
  note "$payload"
  note "产物 → $out_dir"
  ( cd "$out_dir" && shasum -a 256 frame.png layout.json )
}

main() {
  if [ "${1:-}" = "--check" ]; then preflight; note "体检通过"; exit 0; fi
  [ $# -ge 1 ] || die "用法: capture.sh <scene-id> [--out <dir>]"

  local scene_id="$1"; shift
  local out_dir="$REPO_ROOT/tests/goldens/oracle/$scene_id"
  while [ $# -gt 0 ]; do
    case "$1" in
      --out) out_dir="$2"; shift 2 ;;
      *) die "未知参数：$1" ;;
    esac
  done

  preflight
  capture "$scene_id" "$out_dir"
}

main "$@"

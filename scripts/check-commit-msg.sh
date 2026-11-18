#!/bin/bash
# 检查提交标题合不合 docs/dev/commits.md 的规范。本机的 commit-msg 钩子和 CI 查 PR 标题都用它。
#
# 用法：scripts/check-commit-msg.sh <提交信息文件>
#       scripts/check-commit-msg.sh --title '<标题>'
set -euo pipefail

TYPES='feat|fix|perf|refactor|test|docs|build|ci|chore|revert'
SCOPES='core|reader|apple|mac|ios|android|web|drama|audiobook|voices|sync|update|l10n|design|release'
MAX=72

if [ "${1:-}" = "--title" ]; then
  title="${2:-}"
else
  # 第一行不是注释的就是标题（git 会把 # 开头的行去掉）。
  title="$(grep -v '^#' "${1:?要给提交信息文件}" | sed -n '/[^[:space:]]/{p;q;}')"
fi

# 按字符数算长度，不按字节：中文一个字算一个。
for loc in C.UTF-8 en_US.UTF-8 C.utf8; do
  if LC_ALL=$loc true 2>/dev/null && [ "$(LC_ALL=$loc printf '%s' '语' | LC_ALL=$loc wc -m | tr -d ' ')" = 1 ]; then
    export LC_ALL=$loc
    break
  fi
done

fail() {
  {
    echo "提交标题不合规范：$1"
    echo "  $title"
    echo "格式：<类型>(<范围>): <描述>，比如 feat(drama): pick the reference clip by tone"
    echo "类型：${TYPES//|/ }"
    echo "范围：${SCOPES//|/ }"
    echo "详见 docs/dev/commits.md"
  } >&2
  exit 1
}

# rebase 自动合并用的临时标题，合并时会消失。
[[ "$title" =~ ^(fixup|squash|amend)!\  ]] && exit 0

[ -n "$title" ] || fail "没有标题"
[[ "$title" =~ ^($TYPES)(\(([a-z0-9-]+)\))?(!)?:\ (.*)$ ]] || fail "开头要是 类型(范围): "
scope="${BASH_REMATCH[3]}"
desc="${BASH_REMATCH[5]}"
if [ -n "$scope" ] && ! [[ "$scope" =~ ^($SCOPES)$ ]]; then
  fail "没有“${scope}”这个范围"
fi
[ -n "${desc// }" ] || fail "冒号后面要有描述"
printf '%s' "$title" | LC_ALL=C grep -q '[^ -~]' && fail "提交信息用英文"
[[ "$desc" =~ ^\  ]] && fail "冒号后面只空一格"
[[ "$desc" =~ [。.！!]$ ]] && fail "描述末尾不加句号"
len=$(printf '%s' "$title" | wc -m | tr -d ' ')
[ "$len" -le $MAX ] || fail "整行 $len 个字符，超过 $MAX"
exit 0

#!/bin/bash
# 发一版：打三端的包，从 CHANGELOG.md 生成更新要用的文件，建一个 GitHub 草稿 Release。
# 草稿不会被 App 查到（App 查的是最新一个正式 Release），在 GitHub 网页上点“发布”后才算发出去。
#
# 用法：scripts/publish-release.sh            打包、生成，建草稿 Release
#       scripts/publish-release.sh --beta 2   测试版：标签 v<版本>-beta.2，Release 标成预发布。
#           只有“更新渠道”选了测试版的 App 查得到它。编号照样要加一，正式版的编号要比它所有测试版都大。
#       scripts/publish-release.sh --cnb      同时把代码与草稿 Release 同步到 CNB.cool（国内高速镜像）
#       scripts/publish-release.sh --local    只生成到 dist/release-<版本>/，不碰 GitHub；本机试更新用：
#           RELEASE_BASE=http://localhost:8000 scripts/publish-release.sh --local
#           (cd dist/release-<版本> && python3 -m http.server 8000)
#           Mac：open -a 语阅 --args -YYUpdateBase http://localhost:8000/
#           Android 模拟器：RELEASE_BASE 用 http://10.0.2.2:8000，再 -e updateBase http://10.0.2.2:8000/
#
# 环境变量：
#   RELEASE_BASE      下载地址前缀，默认这一版在 GitHub 上的下载地址
#   SPARKLE_KEY_FILE  Mac 更新包的签名私钥（Ed25519，base64），默认 ~/.config/yuyue/sparkle-ed25519.key
#                     公钥在 apple/project.yml 的 SPARKLE_PUBLIC_KEY；私钥丢了，已装的 Mac 版就收不到更新
#   SKIP_PACKAGE=1    用 dist/ 里已经打好的包，不重新打
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIST="$ROOT/dist"
REPO="inokoru/yuyue"
LOCAL=0
SYNC_CNB=0
BETA=""
while [ $# -gt 0 ]; do
  case "$1" in
    --local) LOCAL=1 ;;
    --cnb) SYNC_CNB=1 ;;
    --beta) BETA="${2:?--beta 后面要写第几个测试版}"; shift ;;
    *) echo "不认识的参数：$1" >&2; exit 1 ;;
  esac
  shift
done

VERSION="$(sed -n 's/^ *MARKETING_VERSION: *//p' "$ROOT/apple/project.yml" | head -1)"
BUILD="$(sed -n 's/^ *CURRENT_PROJECT_VERSION: *//p' "$ROOT/apple/project.yml" | head -1)"
CODE="$(sed -n 's/^ *versionCode = \([0-9]*\)/\1/p' "$ROOT/android/app/build.gradle.kts" | head -1)"
KEY="${SPARKLE_KEY_FILE:-$HOME/.config/yuyue/sparkle-ed25519.key}"
SHOWN="$VERSION${BETA:+-beta.$BETA}" # 更新提示里显示的版本，也是标签去掉 v
TAG="v$SHOWN"
BASE="${RELEASE_BASE:-https://github.com/$REPO/releases/download/$TAG}"
BASE="${BASE%/}"
OUT="$DIST/release-$VERSION"

# 每发一版，两边的编号一起加一：Mac 的更新按 CURRENT_PROJECT_VERSION 比新旧，Android 按 versionCode。
if [ "$BUILD" != "$CODE" ]; then
  echo "编号不一致：apple/project.yml 的 CURRENT_PROJECT_VERSION 是 ${BUILD}，android 的 versionCode 是 $CODE" >&2
  exit 1
fi
[ -f "$KEY" ] || { echo "没有签名私钥：$KEY" >&2; exit 1; }

yuyue() { cargo run -q --release --manifest-path "$ROOT/core/Cargo.toml" -p yuyue-cli -- "$@"; }

# 没写这一版的更新日志就不发。
yuyue release-notes "$ROOT/CHANGELOG.md" "$VERSION" md > /dev/null

if [ "${SKIP_PACKAGE:-}" != 1 ]; then
  "$ROOT/scripts/package-release.sh" mac ios android
fi

DMG="$DIST/Yuyue-$VERSION-mac-arm64.dmg"
DMG_X64="$DIST/Yuyue-$VERSION-mac-x86_64.dmg"
IPA="$DIST/Yuyue-$VERSION-ios-unsigned.ipa"
APK="$DIST/Yuyue-$VERSION-android.apk"
APP="$ROOT/apple/build/release-mac/Build/Products/Release/Yuyue.app"
APP_X64="$ROOT/apple/build/release-mac-x86_64/Build/Products/Release/Yuyue.app"
if [ ! -f "$APK" ]; then
  if [ "$LOCAL" = 1 ] && [ -f "$DIST/Yuyue-$VERSION-android-test.apk" ]; then
    APK="$DIST/Yuyue-$VERSION-android-test.apk"
  else
    echo "没有正式签名的 APK（要 android/keystore.properties）。本机试可以加 --local。" >&2
    exit 1
  fi
fi
for f in "$DMG" "$DMG_X64" "$IPA" "$APP" "$APP_X64"; do [ -e "$f" ] || { echo "缺 $f" >&2; exit 1; }; done

rm -rf "$OUT"
mkdir -p "$OUT"

# —— Mac：Sparkle 用的 zip、签名、更新日志页、appcast.xml（Apple 芯片）与 appcast-x86_64.xml（Intel） ——
# 发布附件统一用不带版本号的固定文件名，使 /releases/latest/download/Yuyue-* 永久指向最新版。
ZIP="Yuyue-mac-arm64.zip"
ZIP_X64="Yuyue-mac-x86_64.zip"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$OUT/$ZIP"
ditto -c -k --sequesterRsrc --keepParent "$APP_X64" "$OUT/$ZIP_X64"
SIGN_UPDATE="$(find "$ROOT/apple/build/release-mac/SourcePackages/artifacts" "$ROOT/apple/build/release-mac-x86_64/SourcePackages/artifacts" -path '*Sparkle*/bin/sign_update' -type f 2>/dev/null | head -1)"
[ -n "$SIGN_UPDATE" ] || { echo "找不到 Sparkle 的 sign_update（先打一次 Mac 包）" >&2; exit 1; }
SIG="$("$SIGN_UPDATE" --ed-key-file "$KEY" "$OUT/$ZIP")" # sparkle:edSignature="…" length="…"
SIG_X64="$("$SIGN_UPDATE" --ed-key-file "$KEY" "$OUT/$ZIP_X64")"

page() { # 语言 标题 → 更新窗口里显示的那页（字体、纸色和 App 一样，深色跟系统）
  cat <<HTML
<!doctype html>
<html lang="$1"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>$2</title>
<style>
  :root { color-scheme: light dark; --bg: #F8F5F0; --t1: #2A2B2E; --t3: #8A8682; }
  @media (prefers-color-scheme: dark) { :root { --bg: #1A1613; --t1: #EDEDEE; --t3: #8E8C89; } }
  body { margin: 0; padding: 18px 22px; background: var(--bg); color: var(--t1);
         font: 14px/1.75 "Songti SC", "STSong", serif; -webkit-font-smoothing: antialiased; }
  h1 { font-size: 17px; font-weight: 600; margin: 0 0 2px; }
  .date { color: var(--t3); font: 12px -apple-system, sans-serif; margin-bottom: 12px; }
  ul { margin: 0; padding-left: 1.1em; }
  li { margin: 0 0 6px; }
</style></head><body>
<h1>$2</h1>
<div class="date">$3</div>
$4
</body></html>
HTML
}
DATE="$(grep -m1 "^## v\{0,1\}$VERSION " "$ROOT/CHANGELOG.md" | sed 's/^## [^ ]* [·—-] //')"
page zh-Hans "语阅 $VERSION" "$DATE" "$(yuyue release-notes "$ROOT/CHANGELOG.md" "$VERSION" html-zh)" > "$OUT/notes-zh.html"
page en "Yuyue $VERSION" "$DATE" "$(yuyue release-notes "$ROOT/CHANGELOG.md" "$VERSION" html-en)" > "$OUT/notes-en.html"

PUBDATE="$(LC_ALL=C date -u '+%a, %d %b %Y %H:%M:%S +0000')"
write_appcast() { # out-file zip-name sig-attrs
  cat > "$1" <<XML
<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle" xmlns:dc="http://purl.org/dc/elements/1.1/">
  <channel>
    <title>语阅</title>
    <item>
      <title>$SHOWN</title>
      <pubDate>$PUBDATE</pubDate>
      <sparkle:version>$BUILD</sparkle:version>
      <sparkle:shortVersionString>$VERSION</sparkle:shortVersionString>
      <sparkle:minimumSystemVersion>14.0</sparkle:minimumSystemVersion>
      <sparkle:releaseNotesLink>$BASE/notes-en.html</sparkle:releaseNotesLink>
      <sparkle:releaseNotesLink xml:lang="zh-Hans">$BASE/notes-zh.html</sparkle:releaseNotesLink>
      <sparkle:fullReleaseNotesLink>https://github.com/$REPO/blob/main/CHANGELOG.md</sparkle:fullReleaseNotesLink>
      <enclosure url="$BASE/$2" type="application/octet-stream" $3 />
    </item>
  </channel>
</rss>
XML
}
write_appcast "$OUT/appcast.xml" "$ZIP" "$SIG"
write_appcast "$OUT/appcast-x86_64.xml" "$ZIP_X64" "$SIG_X64"

# —— Android、iPhone：update.json ——
APK_NAME="Yuyue-android.apk"
cp "$APK" "$OUT/$APK_NAME"
SIZE="$(stat -f %z "$OUT/$APK_NAME")"
SHA="$(shasum -a 256 "$OUT/$APK_NAME" | cut -d' ' -f1)"
PAGE="https://github.com/$REPO/releases/tag/$TAG"
yuyue update-json "$ROOT/CHANGELOG.md" "$VERSION" "$CODE" "$PAGE" "$BASE/$APK_NAME" "$SIZE" "$SHA" "$BASE/CHANGELOG.md" > "$OUT/update.json"
# 测试版和它的正式版同一个版本号，更新提示里要分得开：显示成 0.2.0-beta.1。新旧按编号比，不看这个。
if [ -n "$BETA" ]; then
  python3 - "$OUT/update.json" "$SHOWN" <<'PY'
import json, sys
path, shown = sys.argv[1], sys.argv[2]
u = json.load(open(path, encoding="utf-8"))
u["version"] = shown
json.dump(u, open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
fi

cp "$DMG" "$OUT/Yuyue-mac-arm64.dmg"
cp "$DMG_X64" "$OUT/Yuyue-mac-x86_64.dmg"
cp "$IPA" "$OUT/Yuyue-ios-unsigned.ipa"
cp "$ROOT/CHANGELOG.md" "$OUT/"
yuyue release-notes "$ROOT/CHANGELOG.md" "$VERSION" md > "$DIST/release-$VERSION-notes.md"

if [ "$LOCAL" = 1 ]; then
  echo "生成在 ${OUT}（没有上传）"
  exit 0
fi

# 只建草稿：在网页上看过、自己试过更新，再点“发布”。
gh release create "$TAG" --repo "$REPO" --draft ${BETA:+--prerelease} --target main \
  --title "语阅 $VERSION${BETA:+ 测试版 $BETA}" --notes-file "$DIST/release-$VERSION-notes.md" "$OUT"/*
echo "草稿 Release 建好了：${TAG}。在 GitHub 上点“发布”之前，App 都查不到它。"
if [ "$SYNC_CNB" = 1 ]; then
  "$ROOT/scripts/sync-cnb.py" "$VERSION" ${BETA:+--beta "$BETA"} --draft
  echo "CNB 草稿也同步好了；确认无误后运行 scripts/sync-cnb.py $VERSION ${BETA:+--beta $BETA} 即可正式公开。"
else
  echo "确认发布后，运行 scripts/sync-cnb.py $VERSION ${BETA:+--beta $BETA} 同步到 CNB 国内高速镜像。"
fi

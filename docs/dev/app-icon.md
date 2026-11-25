# App icon

All six Apple app icons come from 关于's 64-unit 阅 logo geometry (bold Songti SC 阅 centred
horizontally and 3 units high, over the 24×2.5 capsule rule 13 units up from the bottom):

| Asset name | Chinese | English | Role |
| :--- | :--- | :--- | :--- |
| `AppIcon` | 宣纸 | Rice paper | Primary default (Liquid Glass floating) |
| `AppIconNight` | 暗夜 | Night | Always-dark obsidian edition |
| `AppIconCamelCloth` | 暖纸 | Paper | Hardcover bookcloth (`YYTint.camel`) |
| `AppIconSageCloth` | 草木 | Sage | Hardcover bookcloth (`YYTint.sage`) |
| `AppIconMistCloth` | 烟雨 | Mist | Hardcover bookcloth (`YYTint.mist`) |
| `AppIconBlushCloth` | 海棠 | Blush | Hardcover bookcloth (`YYTint.blush`) |

Regenerate after changing the logo or palette:

```sh
swift scripts/make_app_icon.swift
cd apple && xcodegen
```

The script writes two sets of files:

- `apple/Yuyue/<Name>.icon` — Icon Composer bundles used on iOS 26+ and macOS 26+. Each bundle
  contains `Light`, `Dark`, and `Tinted` layer groups with Liquid Glass specular/shadow parameters
  so default, dark, clear, and tinted system appearances all maintain legible contrast.
- `apple/Yuyue/Assets.xcassets/<Name>.appiconset` — fallback PNGs for iOS 17–25 (Light, Dark, and
  Tinted 1024×1024) and macOS 14–15 (`AppIcon.appiconset` 16–1024px on Apple's 824-of-1024 grid).

`apple/project.yml` sets `ASSETCATALOG_COMPILER_APPICON_NAME: AppIcon` and
`ASSETCATALOG_COMPILER_INCLUDE_ALL_APPICON_ASSETS: YES`, so `actool` compiles all six icons and
populates `CFBundleAlternateIcons` on iOS/iPadOS automatically.

In-app icon picker thumbnails live in `apple/Yuyue/Assets.xcassets/IconPreview_<Name>.imageset`
(rendered via Xcode's `ictool` for iOS Light/Dark `@3x` and macOS Light/Dark `@2x`). On iOS,
`PhoneAppearance` switches icons through `UIApplication.shared.setAlternateIconName(_:)`. On macOS,
selecting 宣纸 sets `NSApp.applicationIconImage = nil` to preserve native macOS 26 dynamic Liquid
Glass, while alternate icons render onto an 824/1024 Dock grid and refresh whenever system or app
appearance changes.

If the Dock or Finder still shows an old icon after a rebuild, that's the system's icon cache:
`lsregister -f` the app, or move it once.

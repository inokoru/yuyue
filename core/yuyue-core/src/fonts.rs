//! 字体: the faces a book can be read in beyond the four built in (黑、宋、楷、霞鹜文楷).
//!
//! Three kinds, named by the id kept in the reader settings (`fontFace`):
//! - an open-source face from the catalog below (`notoserif`), downloaded into the fonts folder;
//! - a file the reader added (`file:<name>`), copied into the fonts folder;
//! - a face installed on the device (`local:<family>`), used by its family name.
//!
//! The settings also keep `font`, the built-in face it reads like, so a device without the face
//! (or an older app) shows something close. 霞鹜文楷 stays `Font::WenKai`, as before.

use std::path::Path;

use crate::document::Font;

/// The look a face belongs to, for 字样册's groups and for the built-in face it falls back to.
#[derive(Clone, Copy, Debug, PartialEq, Eq, uniffi::Enum)]
pub enum FontKind {
    Song,
    Kai,
    Hei,
    Fang,
    Hand,
}

impl FontKind {
    /// The built-in face a page uses until (or instead of) this one.
    pub fn fallback(self) -> Font {
        match self {
            FontKind::Song | FontKind::Fang => Font::Serif,
            FontKind::Kai | FontKind::Hand => Font::Kai,
            FontKind::Hei => Font::Sans,
        }
    }
}

/// One file of a downloadable face.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct FontFile {
    pub url: String,
    /// Its name in the fonts folder.
    pub file: String,
    /// CSS weight range, `"100 450"`.
    pub weight: String,
    pub bytes: u64,
}

/// A face the app can download.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct FontPack {
    /// Kept in the settings; `wenkai` is `Font::WenKai` rather than a `fontFace`.
    pub id: String,
    pub name: String,
    pub kind: FontKind,
    pub license: String,
    pub files: Vec<FontFile>,
    /// Its family on Google Fonts, which serves a few characters of it, so 字样册 can show what
    /// it looks like before the whole face is here.
    pub preview_family: Option<String>,
}

impl FontPack {
    pub fn bytes(&self) -> u64 {
        self.files.iter().map(|f| f.bytes).sum()
    }
}

/// The downloadable faces, in the order 字样册 lists them.
#[uniffi::export]
pub fn font_catalog() -> Vec<FontPack> {
    let tr = crate::i18n::tr;
    let gh = |repo: &str, path: &str| format!("https://github.com/{repo}/{path}");
    vec![
        FontPack {
            id: "wenkai".into(),
            name: tr("霞鹜文楷", "LXGW WenKai"),
            kind: FontKind::Kai,
            license: "SIL OFL".into(),
            files: vec![
                FontFile { url: gh("lxgw/LxgwWenKai-Lite", "releases/download/v1.530/LXGWWenKaiLite-Regular.ttf"), file: "LXGWWenKaiLite-Regular.ttf".into(), weight: "100 450".into(), bytes: 13_949_472 },
                FontFile { url: gh("lxgw/LxgwWenKai-Lite", "releases/download/v1.530/LXGWWenKaiLite-Medium.ttf"), file: "LXGWWenKaiLite-Medium.ttf".into(), weight: "451 900".into(), bytes: 13_801_408 },
            ],
            preview_family: None,
        },
        FontPack {
            id: "notoserif".into(),
            name: tr("思源宋体", "Source Han Serif"),
            kind: FontKind::Song,
            license: "SIL OFL".into(),
            files: vec![
                FontFile { url: gh("notofonts/noto-cjk", "raw/main/Serif/SubsetOTF/SC/NotoSerifSC-Regular.otf"), file: "NotoSerifSC-Regular.otf".into(), weight: "100 550".into(), bytes: 11_625_800 },
                FontFile { url: gh("notofonts/noto-cjk", "raw/main/Serif/SubsetOTF/SC/NotoSerifSC-Bold.otf"), file: "NotoSerifSC-Bold.otf".into(), weight: "551 900".into(), bytes: 12_094_336 },
            ],
            preview_family: Some("Noto Serif SC".into()),
        },
        FontPack {
            id: "notosans".into(),
            name: tr("思源黑体", "Source Han Sans"),
            kind: FontKind::Hei,
            license: "SIL OFL".into(),
            files: vec![
                FontFile { url: gh("notofonts/noto-cjk", "raw/main/Sans/SubsetOTF/SC/NotoSansSC-Regular.otf"), file: "NotoSansSC-Regular.otf".into(), weight: "100 550".into(), bytes: 8_331_336 },
                FontFile { url: gh("notofonts/noto-cjk", "raw/main/Sans/SubsetOTF/SC/NotoSansSC-Bold.otf"), file: "NotoSansSC-Bold.otf".into(), weight: "551 900".into(), bytes: 8_543_168 },
            ],
            preview_family: Some("Noto Sans SC".into()),
        },
    ]
}

fn quote_family(s: &str) -> String {
    format!("\"{}\"", s.replace(['"', '\\', '<', '>', ';', '{', '}'], ""))
}

/// The CSS family a `fontFace` id is drawn with: its own name in `@font-face`, so it never
/// collides with a face of the same name installed on the device.
pub fn css_family(face: &str) -> Option<String> {
    if let Some(family) = face.strip_prefix("local:") {
        return (!family.trim().is_empty()).then(|| quote_family(family.trim()));
    }
    if let Some(name) = face.strip_prefix("file:") {
        return safe_file(name).then(|| quote_family(&format!("yy-{face}")));
    }
    font_catalog().iter().any(|f| f.id == face && f.id != "wenkai").then(|| quote_family(&format!("yy-{face}")))
}

/// A bare file name in the fonts folder, a font by its extension.
pub fn safe_file(name: &str) -> bool {
    !name.is_empty()
        && !name.contains(['/', '\\', '"', '\''])
        && !name.starts_with('.')
        && is_font_file(name)
}

pub fn is_font_file(name: &str) -> bool {
    let lower = name.to_ascii_lowercase();
    lower.ends_with(".ttf") || lower.ends_with(".otf") || lower.ends_with(".ttc")
}

/// `@font-face` for every face in the fonts folder: the catalog's under their ids (霞鹜文楷 as
/// "LXGW WenKai", as before), each added file under `file:<name>`.
pub fn faces_css(dir: &Path) -> String {
    let mut css = String::new();
    let mut known = Vec::new();
    for info in font_catalog() {
        for f in &info.files {
            known.push(f.file.clone());
            if !dir.join(&f.file).is_file() {
                continue;
            }
            let family = if info.id == "wenkai" { "\"LXGW WenKai\"".to_string() } else { format!("\"yy-{}\"", info.id) };
            css.push_str(&format!("@font-face{{font-family:{family};src:url(\"/fonts/{}\");font-weight:{};font-display:swap}}", crate::epub::encode_path(&f.file), f.weight));
        }
    }
    if let Ok(entries) = std::fs::read_dir(dir) {
        let mut added: Vec<String> = entries
            .filter_map(|e| e.ok()?.file_name().into_string().ok())
            .filter(|n| safe_file(n) && !known.contains(n))
            .collect();
        added.sort();
        for name in added {
            let family = quote_family(&format!("yy-file:{name}"));
            css.push_str(&format!("@font-face{{font-family:{family};src:url(\"/fonts/{}\");font-display:swap}}", crate::epub::encode_path(&name)));
        }
    }
    css
}

/// The MIME type a font file is served as.
pub fn mime(name: &str) -> &'static str {
    let lower = name.to_ascii_lowercase();
    if lower.ends_with(".otf") {
        "font/otf"
    } else if lower.ends_with(".ttc") {
        "font/collection"
    } else {
        "font/ttf"
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn families_and_faces() {
        assert_eq!(css_family("notoserif").as_deref(), Some("\"yy-notoserif\""));
        assert_eq!(css_family("wenkai"), None, "霞鹜文楷 is Font::WenKai");
        assert_eq!(css_family("local:Yuanti SC").as_deref(), Some("\"Yuanti SC\""));
        assert_eq!(css_family("local:a\";}b").as_deref(), Some("\"ab\""));
        assert_eq!(css_family("file:我的字.ttf").as_deref(), Some("\"yy-file:我的字.ttf\""));
        assert_eq!(css_family("file:../x.ttf"), None);
        assert_eq!(css_family("nope"), None);

        let dir = std::env::temp_dir().join(format!("yy-fonts-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        for f in ["NotoSerifSC-Regular.otf", "LXGWWenKaiLite-Regular.ttf", "我的字.ttf", "notes.txt"] {
            std::fs::write(dir.join(f), b"x").unwrap();
        }
        let css = faces_css(&dir);
        assert!(css.contains("font-family:\"yy-notoserif\";src:url(\"/fonts/NotoSerifSC-Regular.otf\")"));
        assert!(css.contains("font-family:\"LXGW WenKai\""));
        assert!(css.contains("font-family:\"yy-file:我的字.ttf\";src:url(\"/fonts/%E6%88%91%E7%9A%84%E5%AD%97.ttf\")"));
        assert!(!css.contains("notes.txt") && !css.contains("Bold"));

        // Special characters allowed by safe_file (; { } < >) must match between faces_css and css_family.
        let special = "My;Font{1}<2>.ttf";
        std::fs::write(dir.join(special), b"x").unwrap();
        let css = faces_css(&dir);
        let expected_family = css_family(&format!("file:{special}")).unwrap();
        assert!(
            css.contains(&format!("font-family:{expected_family};")),
            "faces_css family must match css_family ({expected_family}): {css}"
        );
        std::fs::remove_dir_all(&dir).ok();
    }
}

//! 反馈. The report goes up as it will be shown: 语阅 is open source and every
//! report becomes a public GitHub Issue, so the words, the category, the version and
//! the OS travel as plain JSON (`v: 2`). No email or contact info is collected. What
//! is not for the public is sealed here to one public key: every attachment that is
//! not a screenshot the person agreed to show (another file such as a book that
//! won't open, or a screenshot they kept private).
//!
//! A seal is a libsodium sealed box (`crypto_box_seal`): anyone can write one, only
//! the private key can open it. That key stays on the developer's own Mac, not in the
//! app and not in the cloud relay; the relay only carries the sealed bytes.

use std::time::{SystemTime, UNIX_EPOCH};

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use crypto_box::aead::OsRng;
use crypto_box::PublicKey;
#[cfg(test)]
use crypto_box::SecretKey;
use serde::Serialize;

use crate::error::{CoreError, Result};

/// How long the written text may be. ntfy.sh keeps a message under 4096 bytes, so
/// the cap is on the characters and the whole message is checked again at the end.
pub const TEXT_MAX: usize = 800;
/// Attachments per report, and the bytes of one (a screenshot after the app shrinks
/// it, or another file such as a book that won't open). ntfy.sh takes 2 MB per attachment from an
/// anonymous sender; this leaves room for the seal.
pub const FILES_MAX: usize = 3;
pub const FILE_BYTES_MAX: usize = 1_500_000;
const NAME_MAX: usize = 120;
/// First bytes inside a sealed attachment: magic, then the name's length (u16 BE),
/// the name in UTF-8, then the file.
const FILE_MAGIC: &[u8; 4] = b"YYF1";
/// One ntfy message, in bytes.
const MESSAGE_MAX: usize = 4096;

/// What the person chose to send. The apps fill `app_version`, `platform` and
/// `os_version` from the system; they are not identifiers.
#[derive(Clone, Debug, uniffi::Record)]
pub struct FeedbackDraft {
    /// `bug`, `idea` or `other`.
    pub category: String,
    pub text: String,
    pub app_version: String,
    pub platform: String,
    pub os_version: String,
    /// ntfy message ids of the attachments already sent, in order: sealed ones
    /// ([`seal_feedback_file`]) and public screenshots alike. Empty for none.
    pub files: Vec<String>,
}

#[derive(Serialize)]
struct Payload<'a> {
    v: u32,
    category: &'a str,
    text: &'a str,
    app: &'a str,
    platform: &'a str,
    os: &'a str,
    #[serde(rename = "sentAt")]
    sent_at: u64,
    #[serde(skip_serializing_if = "<[String]>::is_empty")]
    files: &'a [String],
}

/// The ntfy message for `draft`: plain JSON (`{"v":2,…}`), ready to POST as the body
/// of one ntfy message. Everything in the message is what the public Issue will show.
#[uniffi::export]
pub fn feedback_message(draft: FeedbackDraft) -> Result<String> {
    let text = draft.text.trim();
    if text.is_empty() {
        return Err(CoreError::Invalid { detail: "empty".into() });
    }
    if text.chars().count() > TEXT_MAX {
        return Err(CoreError::Invalid { detail: "too long".into() });
    }
    let category = match draft.category.as_str() {
        "bug" | "idea" | "other" => draft.category.as_str(),
        _ => return Err(CoreError::Invalid { detail: "category".into() }),
    };
    if draft.files.len() > FILES_MAX || !draft.files.iter().all(|id| is_ntfy_id(id)) {
        return Err(CoreError::Invalid { detail: "files".into() });
    }
    let payload = Payload {
        v: 2,
        category,
        text,
        app: &clip(&draft.app_version, 80),
        platform: &clip(&draft.platform, 40),
        os: &clip(&draft.os_version, 80),
        sent_at: now_ms(),
        files: &draft.files,
    };
    let body = serde_json::to_string(&payload)?;
    if body.len() > MESSAGE_MAX {
        return Err(CoreError::Invalid { detail: "too long".into() });
    }
    Ok(body)
}

/// Seal one private attachment with its name; both are inside the box, so neither
/// shows on ntfy or in the Issue. A screenshot comes here already shrunk and
/// re-encoded by the app (so it carries no location or camera data) when the person
/// chose to keep it private; a public one goes up as the plain JPEG instead. The
/// result is the raw sealed box, sent as an ntfy attachment.
#[uniffi::export]
pub fn seal_feedback_file(public_key_b64: String, name: String, data: Vec<u8>) -> Result<Vec<u8>> {
    if data.is_empty() || data.len() > FILE_BYTES_MAX {
        return Err(CoreError::Invalid { detail: "file".into() });
    }
    // Only the last path part, no control characters, at most NAME_MAX chars.
    let base = name.rsplit(['/', '\\']).next().unwrap_or("");
    let name: String = base.chars().filter(|c| !c.is_control()).take(NAME_MAX).collect();
    let name = if name.trim().is_empty() { "file".to_string() } else { name.trim().to_string() };
    let mut plain = Vec::with_capacity(6 + name.len() + data.len());
    plain.extend_from_slice(FILE_MAGIC);
    plain.extend_from_slice(&(name.len() as u16).to_be_bytes());
    plain.extend_from_slice(name.as_bytes());
    plain.extend_from_slice(&data);
    public_key(&public_key_b64)?.seal(&mut OsRng, &plain).map_err(|_| CoreError::Invalid { detail: "seal".into() })
}

fn public_key(text: &str) -> Result<PublicKey> {
    let key = decode_key(text)?;
    PublicKey::from_slice(&key).map_err(|_| CoreError::Invalid { detail: "public key".into() })
}

fn is_ntfy_id(id: &str) -> bool {
    !id.is_empty() && id.len() <= 32 && id.bytes().all(|b| b.is_ascii_alphanumeric())
}

fn decode_key(text: &str) -> Result<[u8; 32]> {
    let cleaned: String = text.chars().filter(|c| !c.is_whitespace()).collect();
    let bytes = STANDARD.decode(cleaned.as_bytes()).map_err(|_| CoreError::Invalid { detail: "public key".into() })?;
    bytes.try_into().map_err(|_| CoreError::Invalid { detail: "public key".into() })
}

fn clip(text: &str, max: usize) -> String {
    text.trim().chars().take(max).collect()
}

fn now_ms() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_millis() as u64).unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn draft(text: &str, files: Vec<String>) -> FeedbackDraft {
        FeedbackDraft {
            category: "bug".into(),
            text: text.into(),
            app_version: "0.1.0 (1)".into(),
            platform: "ios".into(),
            os_version: "iOS 18.2".into(),
            files,
        }
    }

    fn keys() -> (SecretKey, String) {
        let secret = SecretKey::generate(&mut OsRng);
        let public_key = STANDARD.encode(secret.public_key().as_bytes());
        (secret, public_key)
    }

    fn message(draft: FeedbackDraft) -> serde_json::Value {
        let body = feedback_message(draft).unwrap();
        assert!(body.len() <= MESSAGE_MAX);
        serde_json::from_str(&body).unwrap()
    }

    #[test]
    fn words_go_plain_with_no_contact_field() {
        let json = message(draft("翻页会卡住", vec![]));
        assert_eq!(json["v"], 2);
        assert_eq!(json["category"], "bug");
        assert_eq!(json["text"], "翻页会卡住");
        assert_eq!(json["platform"], "ios");
        assert!(json["sentAt"].as_u64().unwrap() > 0);
        assert!(json.get("files").is_none());
        assert!(json.get("contact").is_none());
    }

    #[test]
    fn carries_file_ids_and_seals_files() {
        let (secret, key) = keys();
        let json = message(draft("截图里那页", vec!["aB3xYz".into(), "Q9".into()]));
        assert_eq!(json["files"], serde_json::json!(["aB3xYz", "Q9"]));
        assert!(feedback_message(draft("x", vec!["../etc".into()])).is_err());

        let log = b"line 1\nline 2\n".to_vec();
        let sealed = seal_feedback_file(key.clone(), "../../日志.txt".into(), log.clone()).unwrap();
        let plain = secret.unseal(&sealed).unwrap();
        let name = "日志.txt".as_bytes();
        assert_eq!(&plain[..4], b"YYF1");
        assert_eq!(u16::from_be_bytes([plain[4], plain[5]]) as usize, name.len());
        assert_eq!(&plain[6..6 + name.len()], name);
        assert_eq!(&plain[6 + name.len()..], &log[..]);
        let blank = secret.unseal(&seal_feedback_file(key.clone(), " \n".into(), log).unwrap()).unwrap();
        assert_eq!(&blank[6..10], b"file");
        assert!(seal_feedback_file(key.clone(), "a".into(), vec![]).is_err());
        assert!(seal_feedback_file(key, "a".into(), vec![0u8; FILE_BYTES_MAX + 1]).is_err());
    }

    #[test]
    fn rejects_empty_long_and_a_bad_key() {
        assert!(feedback_message(draft("  ", vec![])).is_err());
        assert!(feedback_message(draft(&"字".repeat(TEXT_MAX + 1), vec![])).is_err());
        // The longest that fits: every char 4 bytes, three files.
        let files = vec!["a".repeat(32), "b".repeat(32), "c".repeat(32)];
        let body = feedback_message(draft(&"😀".repeat(TEXT_MAX), files)).unwrap();
        assert!(body.len() <= MESSAGE_MAX);
        assert!(seal_feedback_file("not-a-key".into(), "a.log".into(), b"hi".to_vec()).is_err());
    }

    /// What the app seals is what PyNaCl (libsodium) opens on the Mac.
    #[test]
    fn matches_libsodium() {
        let probe = std::process::Command::new("python3").arg("-c").arg("import nacl").status();
        if !probe.map(|s| s.success()).unwrap_or(false) {
            eprintln!("skip libsodium interop: pynacl is not installed");
            return;
        }
        let (secret, key) = keys();
        let sealed = seal_feedback_file(key, "yuyue.log".into(), b"from rust".to_vec()).unwrap();
        let script = r#"
import base64, sys
from nacl.public import SealedBox, PrivateKey
sk = PrivateKey(base64.b64decode(sys.argv[1]))
plain = SealedBox(sk).decrypt(base64.b64decode(sys.argv[2]))
size = int.from_bytes(plain[4:6], "big")
print(plain[6:6+size].decode(), plain[6+size:].decode())
"#;
        let out = std::process::Command::new("python3")
            .arg("-c")
            .arg(script)
            .arg(STANDARD.encode(secret.to_bytes()))
            .arg(STANDARD.encode(&sealed))
            .output()
            .expect("python");
        assert!(out.status.success(), "{}", String::from_utf8_lossy(&out.stderr));
        assert_eq!(String::from_utf8(out.stdout).unwrap().trim(), "yuyue.log from rust");
    }
}

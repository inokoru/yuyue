//! 反馈. The report goes up as it will be shown: 语阅 is open source and every
//! report (title, body, optional device info, and all attachments) becomes a
//! public GitHub Issue so anyone in the community can help reproduce and fix it.
//! No email or contact info is collected; the form links to `mailto:` for
//! private inquiries.

use std::time::{SystemTime, UNIX_EPOCH};

use serde::Serialize;

use crate::error::{CoreError, Result};

/// Maximum characters for the single-line title. Enforced on input so the relay
/// never truncates the user's words in the Issue title.
pub const TITLE_MAX: usize = 60;
/// How long the written description may be. ntfy.sh keeps a message under 4096 bytes,
/// so the cap is on the characters and the whole message is checked again at the end.
pub const TEXT_MAX: usize = 800;
/// Attachments per report, and the bytes of one (a screenshot after the app shrinks
/// it, or another file such as a book or log). ntfy.sh takes 2 MB per attachment from
/// an anonymous sender.
pub const FILES_MAX: usize = 3;
pub const FILE_BYTES_MAX: usize = 1_500_000;
/// One ntfy message, in bytes.
const MESSAGE_MAX: usize = 4096;

/// What the person chose to send. The apps fill `app_version`, `platform` and
/// `os_version` from the system; they are not identifiers.
#[derive(Clone, Debug, uniffi::Record)]
pub struct FeedbackDraft {
    /// `bug`, `idea` or `other`.
    pub category: String,
    /// Required single-line summary, at most `TITLE_MAX` chars; never truncated.
    pub title: String,
    /// Required detailed description, at most `TEXT_MAX` chars.
    pub text: String,
    pub app_version: String,
    pub platform: String,
    pub os_version: String,
    /// ntfy message ids of the attachments already sent, in order. Empty for none.
    pub files: Vec<String>,
}

#[derive(Serialize)]
struct Payload<'a> {
    v: u32,
    category: &'a str,
    title: &'a str,
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
    let title: String = draft.title.split_whitespace().collect::<Vec<_>>().join(" ");
    if title.is_empty() {
        return Err(CoreError::Invalid { detail: "empty title".into() });
    }
    if title.chars().count() > TITLE_MAX {
        return Err(CoreError::Invalid { detail: "title too long".into() });
    }
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
        title: &title,
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

fn is_ntfy_id(id: &str) -> bool {
    !id.is_empty() && id.len() <= 32 && id.bytes().all(|b| b.is_ascii_alphanumeric())
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

    fn draft(title: &str, text: &str, files: Vec<String>) -> FeedbackDraft {
        FeedbackDraft {
            category: "bug".into(),
            title: title.into(),
            text: text.into(),
            app_version: "0.1.0 (1)".into(),
            platform: "ios".into(),
            os_version: "iOS 18.2".into(),
            files,
        }
    }

    fn message(draft: FeedbackDraft) -> serde_json::Value {
        let body = feedback_message(draft).unwrap();
        assert!(body.len() <= MESSAGE_MAX);
        serde_json::from_str(&body).unwrap()
    }

    #[test]
    fn words_and_title_go_plain_with_no_contact_field() {
        let json = message(draft("  翻页\n会卡住  ", "打开第三回之后卡死", vec![]));
        assert_eq!(json["v"], 2);
        assert_eq!(json["category"], "bug");
        assert_eq!(json["title"], "翻页 会卡住");
        assert_eq!(json["text"], "打开第三回之后卡死");
        assert_eq!(json["platform"], "ios");
        assert!(json["sentAt"].as_u64().unwrap() > 0);
        assert!(json.get("files").is_none());
        assert!(json.get("contact").is_none());
    }

    #[test]
    fn carries_file_ids_and_validates_them() {
        let json = message(draft("截图里那页", "详细说明", vec!["aB3xYz".into(), "Q9".into()]));
        assert_eq!(json["files"], serde_json::json!(["aB3xYz", "Q9"]));
        assert!(feedback_message(draft("标题", "x", vec!["../etc".into()])).is_err());
        assert!(feedback_message(draft("标题", "x", vec!["a".into(), "b".into(), "c".into(), "d".into()])).is_err());
    }

    #[test]
    fn rejects_empty_and_overlong_title_or_text() {
        assert!(feedback_message(draft("  ", "正文", vec![])).is_err());
        assert!(feedback_message(draft(&"标".repeat(TITLE_MAX + 1), "正文", vec![])).is_err());
        assert!(feedback_message(draft("标题", "  ", vec![])).is_err());
        assert!(feedback_message(draft("标题", &"字".repeat(TEXT_MAX + 1), vec![])).is_err());
        // The longest that fits: every char 4 bytes, max title + max text + three files.
        let files = vec!["a".repeat(32), "b".repeat(32), "c".repeat(32)];
        let body = feedback_message(draft(&"😀".repeat(TITLE_MAX), &"😀".repeat(TEXT_MAX), files)).unwrap();
        assert!(body.len() <= MESSAGE_MAX);
    }
}

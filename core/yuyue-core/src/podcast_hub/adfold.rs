//! Deterministic rule engine that identifies sponsor reads, pre-roll intros, outros, and
//! housekeeping banter so the Audio Magazine reader can fold them into quiet collapsed rows.

use crate::podcast_hub::parse::MagazineSentence;

const AD_SIGNALS: &[&str] = &[
    "brought to you by",
    "sponsored by",
    "this episode is supported by",
    "promo code",
    "use code",
    "use my code",
    "discount code",
    "percent off",
    "% off",
    "free trial",
    "check out our sponsor",
    "thank you to our sponsor",
    "本期节目由",
    "感谢赞助",
    "赞助播出",
    "专属优惠码",
    "点击shownotes里的链接",
    "优惠券",
    "报暗号",
    "下单立减",
    "广告时间",
];

const INTRO_OUTRO_SIGNALS: &[&str] = &[
    "welcome back to",
    "thanks for listening",
    "don't forget to subscribe",
    "leave us a five-star review",
    "rate and review",
    "欢迎收听本期",
    "感谢收听本期",
    "欢迎订阅",
    "在评论区留言",
    "五星好评",
    "下期再见",
];

/// Marks `skip_reason` on sentences that match ad / sponsor / housekeeping patterns.
pub fn mark_skippable_sentences(sentences: &mut [MagazineSentence]) {
    let total = sentences.len();
    if total == 0 {
        return;
    }
    let total_dur = sentences.last().map(|s| s.t1).unwrap_or(0.0);

    for (i, sent) in sentences.iter_mut().enumerate() {
        if sent.skip_reason.is_some() {
            continue;
        }
        let lower = sent.text_orig.to_ascii_lowercase();
        if AD_SIGNALS.iter().any(|sig| lower.contains(sig)) {
            sent.skip_reason = Some("ad".to_string());
            continue;
        }
        let near_start = i < 4 || sent.t0 < 60.0;
        let near_end = i + 4 >= total || (total_dur > 180.0 && total_dur - sent.t1 < 60.0);
        if (near_start || near_end) && INTRO_OUTRO_SIGNALS.iter().any(|sig| lower.contains(sig)) {
            sent.skip_reason = Some(if near_start { "intro".to_string() } else { "outro".to_string() });
        }
    }

    // Bridge single neutral sentences sandwiched between two ad sentences (same sponsor read).
    if total >= 3 {
        let flags: Vec<Option<String>> = sentences.iter().map(|s| s.skip_reason.clone()).collect();
        for i in 1..total - 1 {
            if flags[i].is_none() && flags[i - 1].as_deref() == Some("ad") && flags[i + 1].as_deref() == Some("ad") {
                sentences[i].skip_reason = Some("ad".to_string());
            }
        }
    }
}

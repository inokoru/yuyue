//! 拆场: a section cut where the place, the time or the people change. A scene is what lines
//! are told apart in (who is there) and what the room tone is chosen by.
//!
//! The rules cut at what's written: a break (`***`, `※`, an empty line) and the words that
//! jump in time or place (次日, 却说, Meanwhile). A model, when there is one, gives its own cut
//! with the place and who comes and goes; it's taken only when it covers the section without
//! gaps or overlaps and names only people in the cast.

use serde::{Deserialize, Serialize};

use super::cast::CastTable;
use super::{SceneCrowd, DramaScene, SceneTime, SceneWeather};

const JUMPS: &[&str] = &[
    "次日", "第二日", "第二天", "翌日", "隔日", "三天后", "几天后", "幾天後", "数日后", "數日後", "过了几日", "過了幾日", "却说", "卻說", "且说", "且說", "话分两头", "話分兩頭", "话说", "話說", "再说", "再說",
    "当晚", "當晚", "是夜", "这日", "這日", "那日", "一日", "Meanwhile", "The next morning", "The next day", "That evening", "Later that",
];

fn is_break(s: &str) -> bool {
    let t = s.trim();
    t.is_empty() || t.chars().all(|c| "*＊※-—·• ~～".contains(c))
}

/// The most lines a scene sent to a model holds; longer ones are cut at the rules' cut points.
pub const MAX_LINES: usize = 150;

/// Where the rules cut the section: the first sentence of each scene.
pub fn rule_cuts(sentences: &[String]) -> Vec<u32> {
    let mut cuts = vec![0u32];
    for (i, s) in sentences.iter().enumerate().skip(1) {
        let t = s.trim_start_matches(['　', ' ']);
        if ((is_break(&sentences[i - 1]) && !is_break(s)) || JUMPS.iter().any(|j| t.starts_with(j))) && *cuts.last().unwrap() != i as u32 {
            cuts.push(i as u32);
        }
    }
    cuts
}

/// The rules' scenes, with everyone named in each as present.
pub fn rule_scenes(section: u32, sentences: &[String], cast: &CastTable) -> Vec<DramaScene> {
    if sentences.is_empty() {
        return Vec::new();
    }
    let cuts = rule_cuts(sentences);
    let n = sentences.len() as u32;
    cuts.iter()
        .enumerate()
        .map(|(k, &first)| {
            let last = cuts.get(k + 1).map(|c| c - 1).unwrap_or(n.saturating_sub(1));
            let mut present: Vec<u32> = Vec::new();
            for s in first..=last {
                for h in cast.find(&sentences[s as usize]) {
                    if !present.contains(&h.id) {
                        present.push(h.id);
                    }
                }
            }
            DramaScene { section, first, last, place: String::new(), place_kind: "未知".into(), indoor: None, time: SceneTime::Unknown, weather: SceneWeather::Unknown, crowd: SceneCrowd::Unknown, present }
        })
        .collect()
}

/// A model's scene, as asked for in the attribution request.
#[derive(Clone, Debug, Deserialize, Serialize, PartialEq)]
pub struct ModelScene {
    pub first: u32,
    pub last: u32,
    #[serde(default)]
    pub place: String,
    #[serde(default)]
    pub place_kind: Option<String>,
    #[serde(default)]
    pub indoor: Option<bool>,
    #[serde(default)]
    pub time: Option<String>,
    #[serde(default)]
    pub weather: Option<String>,
    #[serde(default)]
    pub crowd: Option<String>,
    #[serde(default)]
    pub present: Vec<ModelPresent>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq)]
pub struct ModelPresent {
    pub who: String,
    #[serde(default)]
    pub enter: Option<u32>,
    #[serde(default)]
    pub leave: Option<u32>,
}

/// The places a room tone is chosen by. Anything else is 未知.
pub const PLACE_KINDS: &[&str] = &[
    "居室", "厅堂", "大殿", "书房", "厨房", "客栈酒馆", "茶楼", "店铺", "牢房", "山洞", "船舱", "车厢", "教室", "办公室", "医院", "地下室", "庭院", "街市", "村庄", "田野", "山林", "竹林", "河边", "海边",
    "荒漠", "雪地", "城墙", "战场", "码头", "桥上", "墓地", "夜路", "城市街道", "地铁车站", "咖啡馆", "餐厅", "机场", "车内", "工厂", "酒吧", "梦境", "回忆", "未知",
];

/// A model's scenes for a stretch of sentences [`from`, `to`], checked: they follow one another
/// without gaps or overlaps and cover the stretch; anyone present is in the cast (by a name
/// it knows). Present only ever narrows the rules' list: the model can say someone left, not
/// bring in someone the text doesn't name.
pub fn checked(section: u32, from: u32, to: u32, model: &[ModelScene], rules: &[DramaScene], cast: &CastTable) -> Option<Vec<DramaScene>> {
    let mut at = from;
    for m in model {
        if m.first != at || m.last < m.first || m.last > to {
            return None;
        }
        at = m.last + 1;
    }
    if at != to + 1 {
        return None;
    }
    let mut out = Vec::new();
    for m in model {
        let named: Vec<u32> = rules.iter().filter(|r| r.first <= m.last && r.last >= m.first).flat_map(|r| r.present.iter().copied()).collect();
        let mut present = Vec::new();
        for p in &m.present {
            let id = cast.resolve(&p.who)?;
            if named.contains(&id) && !present.contains(&id) {
                present.push(id);
            }
        }
        // The model left someone out who speaks by name here: keep them.
        if present.is_empty() {
            present = named.clone();
        }
        let pk = m.place_kind.clone().filter(|k| PLACE_KINDS.contains(&k.as_str())).unwrap_or_else(|| "未知".into());
        out.push(DramaScene {
            section,
            first: m.first,
            last: m.last,
            place: m.place.clone(),
            place_kind: pk,
            indoor: m.indoor,
            time: match m.time.as_deref() {
                Some("day") => SceneTime::Day,
                Some("night") => SceneTime::Night,
                Some("dawn") => SceneTime::Dawn,
                Some("dusk") => SceneTime::Dusk,
                _ => SceneTime::Unknown,
            },
            weather: match m.weather.as_deref() {
                Some("clear") => SceneWeather::Clear,
                Some("rain") => SceneWeather::Rain,
                Some("snow") => SceneWeather::Snow,
                Some("wind") => SceneWeather::Wind,
                Some("storm") => SceneWeather::Storm,
                _ => SceneWeather::Unknown,
            },
            crowd: match m.crowd.as_deref() {
                Some("alone") => SceneCrowd::Alone,
                Some("few") => SceneCrowd::Few,
                Some("many") => SceneCrowd::Many,
                _ => SceneCrowd::Unknown,
            },
            present,
        });
    }
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::drama::cast::from_rules;

    fn s(v: &[&str]) -> Vec<String> {
        v.iter().map(|x| x.to_string()).collect()
    }

    #[test]
    fn cuts_at_breaks_and_jumps() {
        let ss = s(&["宝玉来了。", "黛玉道：“你来了。”", "***", "次日一早，宝玉又来。", "却说凤姐那边。"]);
        assert_eq!(rule_cuts(&ss), vec![0, 3, 4]);
    }

    #[test]
    fn a_models_scenes_must_cover_the_section() {
        let cast = from_rules(&["宝玉".into(), "黛玉".into()]);
        let ss = s(&["宝玉来了。", "黛玉道：“你来了。”", "宝玉笑了。"]);
        let rules = rule_scenes(0, &ss, &cast);
        let ok = vec![ModelScene { first: 0, last: 2, place: "潇湘馆".into(), place_kind: Some("居室".into()), indoor: Some(true), time: Some("day".into()), weather: None, crowd: None, present: vec![ModelPresent { who: "宝玉".into(), enter: None, leave: None }, ModelPresent { who: "黛玉".into(), enter: None, leave: None }] }];
        assert!(checked(0, 0, 2, &ok, &rules, &cast).is_some());
        let gap = vec![ModelScene { last: 1, ..ok[0].clone() }];
        assert!(checked(0, 0, 2, &gap, &rules, &cast).is_none());
        let stranger = vec![ModelScene { present: vec![ModelPresent { who: "张飞".into(), enter: None, leave: None }], ..ok[0].clone() }];
        assert!(checked(0, 0, 2, &stranger, &rules, &cast).is_none());
    }
}

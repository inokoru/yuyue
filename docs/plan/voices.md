# 语阅 · 声音体验方案

> 2026-11-16 ｜ 管两件事：平常朗读用什么声音、广播剧在三端（尤其手机）怎么听到最好。
> 和 `drama.md` 9.4 有出入的地方，以本文为准。
> 本文写给实现的人：照着做，不用再问。

## 〇、给实现者

1. **先读再写。** 第九节列了每一步要读的现有文件。类型名、函数名是建议，和现有代码风格冲突时以现有风格为准；**数据的含义、文件格式、验收线不能改**。
2. **核心不联网、不跑模型**（和广播剧方案同一条）。`core/yuyue-core` 只做纯计算：声音卡的读写校验、语气归档、选角、缓存键。跑模型、下载、录音都在各端。
3. **按第八节的步子做，一步一验收。** 第 1 步（试听）不过关线，后面换退路（第十节），不要硬做。
4. **界面先出原型再写代码。** 本文只定界面上有什么、文字是什么，不定样式。动到的界面见 7.6。
5. **不要做的**：不给手机接 Qwen3-TTS；不用任何真人的声音做自带声音；不用 Qwen3-TTS 的九个预设音色；不删系统声音、云端声音、ElevenLabs 克隆这三条现有的路；不改广播剧的认人、导演、混音。
6. 界面文字都要进词表（Apple `Localizable.xcstrings`，Android 的 l10n），中英文都写。

## 一、对用户要做到的

1. **点了就出声，第一句就好听。** 不用先选声音、不用填 Key、不用懂“引擎”。
2. **手机上不花钱、没有 Mac，也能听到分角色、有语气的广播剧。**
3. **同一个人物，在哪台设备上、哪一章，听着都是同一个人。**
4. **克隆自己的声音不要钱、不联网，录音不出这台设备。**
5. **手机不烫、不费电。** 费力的活只在充电时做，或者交给 Mac。

## 二、核心：声音 = 一张声音卡

App 里新加的声音都是同一种东西——**声音卡**：几段 6–10 秒的参考音 + 每段念的文字 + 男女、年纪、几个形容词。合成时让模型**照参考音念**（零样本克隆），不是“选引擎里的一个音色”。

- **哪里都是同一个人**：手机的小模型和 Mac 的大模型照同一段参考音。
- **克隆、造声音、自带声音是一回事**：都是声音卡，走同一条路。
- **换模型不换声音**：以后换更好的模型，声音卡照用。

### 2.1 语气放在参考音里

小模型听不懂“愤怒地说”，但照着念时会把参考音的语气学过去。每张卡最多带**七段**参考音，一段一种语气：

| `style` | 意思 | 造的时候给大模型的说话指令（中文书） |
|---|---|---|
| `neutral` | 平常 | 平稳自然地说 |
| `joy` | 高兴 | 高兴地、带着笑意说 |
| `anger` | 生气 | 生气地、语气很重地说 |
| `sad` | 难过 | 难过地、声音发沉地说 |
| `tense` | 紧张（怕、急、惊） | 紧张地、急促地说 |
| `soft` | 低声（耳语、温柔） | 压低声音、轻轻地说 |
| `shout` | 喊 | 大声喊出来 |

`neutral` 必有，其余可缺；缺的用 `neutral`。

### 2.2 导演稿的语气归到七档（核心做）

输入是现有的 `DramaAct`（`emotion`、`level` 0–3、`volume`…）。从上往下，先中先用：

1. `volume == Shout` → `shout`
2. `volume == Whisper` → `soft`
3. `level <= 1` → `volume == Low` 时 `soft`，否则 `neutral`
4. 按 `emotion`：

| `ActEmotion` | 归到 |
|---|---|
| Neutral | neutral |
| Joy | joy |
| Anger、Disgust | anger |
| Sadness、Weary | sad |
| Fear、Anxious、Surprise | tense |
| Tender | soft |
| Mocking | neutral（小模型学不像冷笑，硬配会出戏） |

快慢（`pace`）、远近（`distance`）、非语言声（`nonverbal`）照现有做法走时间线和混音，不进这张表。

### 2.3 数据

```rust
// core/yuyue-core/src/drama/card.rs（新文件）
pub enum CardStyle { Neutral, Joy, Anger, Sad, Tense, Soft, Shout }

pub enum CardOrigin {
    Builtin,                              // 声音包里的
    Designed { book: String, character: u32 },  // 给某本书的某个人造的
    Cloned,                               // 用户录的
}

pub struct CardClip {
    pub style: CardStyle,
    pub file: String,     // 相对卡所在文件夹，如 "neutral.ogg"
    pub text: String,     // 这段参考音念的字，一字不差
    pub seconds: f32,
}

pub struct VoiceCard {
    pub id: String,            // 见下
    pub name: String,          // 自带的是起好的名字；造的是人物名；克隆的是用户起的
    pub gender: CharacterGender,
    pub age: CharacterAge,
    pub tags: Vec<String>,     // 亮/厚/沙/软/冷/稳 这类，0–3 个
    pub lang: String,          // 参考音是哪门语言："zh"、"en"、"ja"
    pub origin: CardOrigin,
    pub clips: Vec<CardClip>,
    pub description: String,   // 造它时用的描述；克隆的为空
    pub version: u32,          // 参考音重录、重造一次加一，进缓存键
}
```

**声音 id**（接着现有的 `cloud.` `cloned.` `actor.` 前缀；`Voices.find` 多认一种）：

- 自带：`card.b.<两位序号>`，如 `card.b.07`
- 造的：`card.d.<书 id 前 8 位>.<人物 id>`
- 克隆的：沿用 `cloned.<id>`（现有 id 不变，用户已设给人物的不会丢）

**参考音文件**：Ogg Vorbis，单声道，24 kHz，响度归到 −20 LUFS，首尾静音各留 0.1 秒，6–10 秒。核心里已有 Ogg 解码（`drama/library.rs` 用的 lewton），照用。

**核心接口**（都在 `YuyueCore` 上，照现有 `drama_*` 的写法）：

```rust
fn card_style_for(&self, act: DramaAct) -> CardStyle;                       // 2.2
fn cards_builtin(&self) -> Vec<VoiceCard>;                                  // 读登记过的声音包
fn cards_set_pack(&self, dir: String) -> u32;                               // 登记声音包文件夹，回张数；照 drama_set_sound_library
fn cards_of_book(&self, book_id: String) -> Result<Vec<VoiceCard>>;         // 这本书造的
fn card_save(&self, book_id: Option<String>, card: VoiceCard) -> Result<()>; // 造的传书 id，克隆的传 None
fn card_find(&self, id: String) -> Option<VoiceCard>;
fn card_clip_path(&self, id: String, style: CardStyle) -> Option<String>;   // 缺这档时回 neutral 的
fn card_check(&self, samples: Vec<f32>, rate: u32, text: String) -> CardCheck; // 录音合不合格，见 6.2
fn card_voice_info(&self, card: VoiceCard, embedding: Vec<f32>, acts: bool) -> VoiceInfo; // 给选角
```

校验（核心做，不信文件）：`file` 不许指到文件夹外；`seconds` 以解码出来的为准；没有 `neutral` 的卡不认。

### 2.4 存在哪、跟不跟着同步

| 哪种卡 | 存在哪 | 同步 |
|---|---|---|
| 自带 | `models/voices-1/`（下载来的，见第四节） | 不用同步，各端自己下 |
| 造的 | 这本书的文件夹下 `drama/cards/<id>/`：`card.json` + 各段 ogg | **跟书走**，进快照和同步 |
| 克隆的 | 现有位置（Apple：`Application Support/clones/<id>/`；Android 同名文件夹），加 `card.json` 和各段 ogg | 默认不同步；用户打开这张卡的“跟着同步”才同步 |

“跟书走”怎么实现：先读同步的代码（Apple `App/Sync.swift`，核心 `store.rs`、`snapshot.rs`），看它现在带不带书文件夹里的文件。

- 带文件：把 `drama/cards/` 加进去。
- 只带键值：把卡写进键值，实体 `drama/{book}/cards`，字段名是卡 id，值是 `card.json` 的内容加每段音频的 base64（一张卡七段约 200 KB，一本书最多造 24 张）。落盘时再写成文件。

局域网从 Mac 拿片段时（`App/RecordingShare.swift` 的 `/drama`、`/clip`），加一个 `/card`：把这本书造的卡一起给过去，手机即时层念没做好的章节时用同一批卡。

## 三、两层合成：先出声，后变好

| | 即时层 | 精做层 |
|---|---|---|
| 做什么 | 点了马上念，边念边合成 | 提前把整章做好，带细的语气 |
| 模型 | 能照参考音念的小模型（约 100 MB） | Qwen3-TTS 1.7B |
| 哪端 | 三端 | Mac；云端（用户自己接，现有的） |
| 语气 | 七档（照哪段参考音） | 细（说话指令，现有的 `drama_act_instruction`） |
| 质量把关 | 轻（3.3） | 全：现有的回听、声纹 |
| 引擎名（进缓存键，`DramaCaps.engine`） | `live-<模型名>-<版本>` | `studio-qwen3tts-1.7b-<版本>` |

### 3.1 换层的规则

- **换层只在章与章之间。** 一章的片段**全部**有精做层的，这章就用精做层；差一段都整章用即时层。
- 两层的片段缓存键不同（引擎名不同），可以同时留着。
- 核心加一个查询：`drama_section_layer(book_id, section) -> Studio | Live`，播放器进一章时问一次，这一章里不再变。
- 用户换了某人的声音：两层里这个人的片段都作废（现有逻辑按声音 id 作废，照用）。

### 3.2 即时层

**模型**：首选 ZipVoice 蒸馏量化版（中英文，约 104 MB，另要 vocos 24 kHz 声码器）。备选见第十节。最后用哪个由第 1 步试听定；下面按首选写，换模型时只换 3.2 这一层，声音卡和上层不动。

**每端一个类，实现现有的声音接口**（Apple `Voice` 协议，Android `BookVoice` 接口）：

```swift
// apple/Yuyue/Speech/CardVoice.swift（新）
nonisolated struct CardVoice: Voice {
    let card: VoiceCard
    var id: String { card.id }
    var name: String { card.name }
    var qualityName: String? { nil }        // 克隆的回 “克隆”
    var isOffline: Bool { true }
    func speaks(_ language: String) -> Bool // 见下
    func render(_ text: String, language: String) async throws -> Speech?            // neutral
    func render(_ text: String, language: String, style: CardStyle) async throws -> Speech?
}
```

- `speaks`：模型会念的语言都回 true（ZipVoice：中、英）。中文参考音念英文书可以，反过来也可以，但默认旁白按书的语言挑同语言的卡。
- 模型不会的语言（日文等）：这门语言不出声音卡，照旧用系统声音。声音列表里“推荐”一组为空时不显示这组。
- 跑模型：Android 用已有的 onnxruntime（`build.gradle.kts` 里已有 1.30.0）；Apple 用 onnxruntime 的 Swift 包跑同一份 ONNX（三端一份模型，结果一样），**不要**为它另转 Core ML。
- 文字进模型前先过现有的读音替换和 `TextPolish`；一句超过 60 个字（英文 40 个词）时在标点处切开分别合成再接上，接缝留 0.12 秒。
- 模型和参考音的特征缓存：一张卡一档语气的参考音特征第一次用时算好留在内存里，最多留 16 份。
- 输出统一成现有的 `Speech`（Apple）/ `Speech(samples, rate)`（Android），响度拉到 −20 LUFS。

**下载**：照对齐模型那套（Apple `App/Aligner.swift` 的 `ensure`、`installed`、BLAKE3 核对；Android `speech/Aligner.kt`）。文件加进 `scripts/upload-models.sh`，传到同一个 release。新类 `LiveVoiceModel`，放模型地址、大小、BLAKE3。

### 3.3 即时层的轻把关（各端做，不用听写）

合成出一句后查三样，任一不过就重合一次（换随机种子）；还不过，这一句用系统声音念，不打断播放：

1. 时长：偏离“字数 × 这张卡的平均语速”超过 ±50%（平均语速：自带卡在声音包里写好；别的卡用 `neutral` 参考音算）。
2. 静音：开头或结尾 > 0.6 秒，或中间有 > 1.5 秒的静音。
3. 削波：> 0.1% 的采样点顶到满幅。

核心里已有 `drama_measure_clip`，不带听写调用它（`heard` 传空、只看这三样），不要另写一套。

### 3.4 预合成、省电、不烫

- 现有 `Player` 只提前渲染 2 句（`Player.swift` 的 `ahead = 2`）。即时层改成**按时长**：提前合成到播放位置之后 60 秒为止；暂停即停。系统声音、云端声音照旧。
- 热状态（Apple `ProcessInfo.thermalState`；Android `PowerManager.getCurrentThermalStatus`）：
  - 到“serious / SEVERE”：提前量缩到 15 秒。
  - 到“critical / CRITICAL”：这一章剩下的用系统声音念，出一行“手机有点热，这一章先用系统声音”，下一章再试。
- 手机上“整本提前做好”只在充电时跑；拔了电就停，接着的时候续上。
- 即时层合成过的句子留着（按现有片段缓存的位置和格式存），重听、倒回不再合成。每本书占用上限 500 MB，超了从最早听完的章删起。

### 3.5 精做层（Mac）

- 把现在调试开关里的本机声音服务搬进 App：用 mlx-audio 的 Swift 包直接跑 Qwen3-TTS，不带 Python。要的三个模型和现在 `scripts/actor-server/serve.py` 用的一样：造声音（VoiceDesign）、带指令念（现在用的 CustomVoice 不再用）、照参考音念（Base）。
- **精做层也照声音卡念**：用 Base 模型，给它这张卡的参考音和文字。语气分两步试（第 1 步试听时定用哪个）：
  - a. Base 模型如果接受说话指令：`neutral` 参考音 + 现有的 `drama_act_instruction` 给的指令。
  - b. 不接受：照 2.2 挑那一档的参考音念（和即时层同一个办法，只是模型大、念得好）。
- 下载入口：设置的朗读页一行“精做的声音”，写清多大（三个模型共约 6–8 GB，按实际填）、能删。没下时广播剧只有即时层。
- `ActorVoice.swift` 和三个 `-debugActor*` 调试开关留着当开发用，不进界面。

## 四、自带的声音（声音包）

### 4.1 内容

- **48 张卡**：中文 32 张、英文 16 张。每门语言男女各半；年纪：少年 1/8、青年 3/8、中年 3/8、老年 1/8。
- 每张七段参考音。
- 同语言同性别的卡，`neutral` 的声纹两两相似度 < 0.8（用现有的声纹模型量）；不够开的重造。
- 每门语言定两张**默认旁白**（一男一女）：`voices.json` 里 `narrator: true`。挑的标准：念一小时不累、不抢戏、数字和专名念得稳。默认用女声还是男声：中文书默认男、英文书默认女——这只是起点，由用户试听后改 `voices.json`。
- 其余卡 `narrator: false`，但用户可以手动选任何一张当旁白。

### 4.2 怎么做（`scripts/voice-lab/build_voices.py`，新）

照 `scripts/sound-lab/build_library.py` 的写法（可中断续做、出清单文件）：

1. 一个 `voices.yaml` 列 48 行：序号、名字、语言、男女、年纪、形容词、一句描述（如“三十来岁的男人，声音厚、稳，语速不快”）。
2. 每张卡：用 Qwen3-TTS VoiceDesign 按描述念 `neutral` 那句 → 再用 Base 模型照这段 `neutral`、分别带六种指令（2.1 的表）念另外六句。**七段念的字不一样**（每档语气配一句合这个语气的话，中英文各一套，写在脚本里），每句 25–40 个字。
3. 每段：切掉首尾静音、归到 −20 LUFS、存 Ogg。过不了下面的自动检查就换种子重做，最多 5 次：
   - 时长 6–10 秒；没有削波；
   - 用听写（Mac 上现有的）核对念的字，错字率 ≤ 2%；
   - 六段带语气的和 `neutral` 的声纹相似度 ≥ 0.55（带语气会掉，不能掉成另一个人）。
4. 出 `voices-1.zip`：`voices.json` + `b01/neutral.ogg` … 。`voices.json` 每张卡是 2.3 的 `VoiceCard`，另加 `narrator`、`rate`（平均语速，字/秒）、`embedding`（`neutral` 的声纹）。
5. 许可：整包 CC0，`voices.json` 开头写明“全部由模型按文字描述生成，没有用任何真人的录音”和生成用的模型、描述。

大小：48 × 7 × 约 8 秒 ≈ 45 分钟，Ogg 约 15 MB。

### 4.3 下载和登记

- 新类 `VoicePack`，照 `Speech/SoundLibrary.swift`（Apple）和 `speech/SoundLibrary.kt`（Android）：地址、BLAKE3、`ensure`、`register`（调 `cards_set_pack`）。
- 和即时层的模型一起下，算一件事：界面上只说“朗读声音”，不分“模型”和“声音包”。

## 五、各种情况下用户听到什么

### 5.1 平常朗读（三端一样）

1. 第一次点朗读：**马上出声**（系统声音先顶着），同时开始下“朗读声音”（模型 + 声音包，约 120 MB）。
   - 在 Wi-Fi 上：直接下，朗读面板里一行小字“正在准备更好的声音 43%”。
   - 用流量：不自动下，那一行写“更好的声音 · 120 MB · 下载”，点了才下。
   - 下载失败：那一行写“没下下来 · 再试”，不弹窗。
2. 下完后**从下一章起**换成默认旁白（当前这章不换，免得半路变声）。用户此时手动暂停再播，也从那一刻换。
3. 之后新书都直接用默认旁白。
4. 用户之前已经手动选过声音（`player.voice.<语言>` 里有值）：不动他的选择。
5. 没选过的：`Player` 取声音的顺序改成 用户选的 → 默认旁白卡（已下好时）→ 系统声音。

**声音列表**分四组，从上到下：

| 组 | 有什么 | 没有时 |
|---|---|---|
| 推荐 | 自带的卡，默认旁白排最前 | 没下好：一行下载；这门语言没有卡：不显示这组 |
| 我的 | 克隆的、给这本书造的 | 只有一行“克隆一个声音” |
| 云端 | 现有的 | 现有的做法 |
| 系统自带 | 现有的系统声音 | — |

### 5.2 广播剧 · 选角

- 给核心 `drama_cast_voices` 的声音表这样凑（Apple `DramaSynth.voices`，Android `DramaSynth.kt` 里对应的地方）：
  - 即时层装好了：**这门语言的自带卡 + 这本书造的卡**，`acts: true`。不再放系统声音。
  - 没装好：照现在（系统声音）。
  - 旁白选了云端：照现在，把云端的声音也放进去。
  - 克隆的卡**不放**。用户亲手设给人物的（`drama_set_voice` / 人物页）照现有逻辑固定住，选角不动。
- 旁白：用户的朗读声音；是系统声音且即时层装好了，就用默认旁白卡。
- 每张卡的声纹：自带的在声音包里写好了，不用现量；造的、克隆的造好时量一次存下。

### 5.3 广播剧 · 手机

| 用户的情况 | 听到的 |
|---|---|
| 只有手机 | 即时层：每个主要人物一张卡，七档语气，环境音、音效、过门都有。点了就听。 |
| 家里有 Mac | Mac 把后面的章节精做好，手机回到同一个网络时自动拿。没拿到的章即时层念，同一批卡。 |
| 接了云端 | 现有的做法，按字数花钱，开始前给估价。 |

**人物没有合适的卡时**（核心的 `CastingPlan.to_design`）：

- Mac（精做层装好了）：照描述造一张卡，七段一起造（做法同 4.2 第 2、3 步，一张约一两分钟），存进这本书，跟着同步。
- 手机：不造。用最接近的自带卡。等这本书在 Mac 上打开过、卡同步过来，自动换成造的那张——**只在章与章之间换**，并作废这个人即时层的片段。

### 5.4 Mac 当声音工坊

现在：手机打开“整本提前做好”时找附近的 Mac，Mac 做了什么拿什么。改成 Mac 主动做：

1. 手机开着一本书的“听成一部剧”时，定时（每 60 秒，和翻章时）把“书 id + 读到第几章”发给附近自己的 Mac（`RecordingShare` 加 `/want`，带现有的配对 token）。
2. Mac 收到后：书在自己书库里、精做层装好了、Mac 接着电源 → 自动从那一章往后精做，一次最多往后 10 章；手机读过去了就接着往后推。
3. Mac 上不弹任何东西；菜单栏图标的菜单里多一行“正在给手机做《书名》第 n 章”，能点“停”。设置的朗读页一个开关“给我的手机提前做好”，默认开。
4. 手机照现有的 `/drama`、`/clip` 拿，另拿 `/card`。行里的字照旧（“从 Mac 拿做好的片段 n / m”）。
5. Mac 上没有这本书：不做，手机上不提示。

### 5.5 广播剧 · Mac

- 点了就听：即时层。
- “整本提前做好”：精做层，按章往后推（现有逻辑）。

## 六、克隆自己的声音（三端）

### 6.1 流程

1. 声音列表“我的”组里点“克隆一个声音”。
2. 勾“这是我的声音，或者我得到了本人同意”（现有的那句，必勾）。
3. 屏幕上出一段要念的话（中文 35 字左右、英文 25 词左右，按书的语言；核心给，`card_script(lang, style)`），点一下开始录，念完自动停（静音 1.2 秒）或点一下停。也可以导入一段录音——导入的要用户把它念的字打出来，或者用现有的听写转出来给用户改。
4. 录完马上查（6.2）。不合格：写清哪样不行，“再录一次”。合格：自动用它念当前这本书的下一句给用户听，下面两个键“用这个声音”“再录一次”。
5. 起名，存成卡（只有 `neutral`）。
6. 卡的详情里：**“加上语气”**——六档各念一句（照提示的语气念），每录一档马上能试听；可以只录几档。**“跟着同步”**开关，默认关。重命名、删除（删除照现有的确认做法）。

### 6.2 录音合不合格（核心 `card_check`）

| 查什么 | 线 | 不过时说什么 |
|---|---|---|
| 时长 | 6–15 秒 | 太短了，再多念一点 / 太长了，念到提示的话就停 |
| 音量 | 说话部分 −35 到 −8 dBFS | 声音太小，离近一点 / 声音太大，离远一点 |
| 削波 | < 0.1% | 声音破了，离远一点 |
| 底噪 | 说话和不说话的音量差 ≥ 20 dB | 周围有点吵，换个安静的地方 |
| 念的字 | 有听写时：和提示的话错字率 ≤ 15% | 好像没照着念，再试一次 |

Apple 现有的 `ClonedVoice.swift` 里有 `SampleCheck`（约 160 行处），Android `RoleVoices.kt` 里也有同名的；把判断挪进核心，两端只显示。

### 6.3 和现有克隆的关系

- 现有的 `ClonedVoice`（走 ElevenLabs）不删。一张克隆卡可以两样都有：本机的参考音，和 ElevenLabs 那边的 id。
- 念的时候：即时层装好了就用本机；没装好、有 ElevenLabs Key 且那边有 id 就用 ElevenLabs；都没有，旁白念（现有做法）。
- 卡的详情里一行“也用 ElevenLabs 念”，只有填了 Key 才出现，默认关。
- 已有的克隆声音（只有 `sample.m4a`）：第一次打开声音列表时在本机转成卡（转 Ogg；`text` 为空的用听写补，补不出来就标“要重录”）。

## 七、细则

### 7.1 广播剧里一段怎么合成（两端的 `DramaSynth.make`）

拿到 `ClipAsk` 后按声音 id 的前缀分：

| 前缀 | 怎么念 |
|---|---|
| `card.` / `cloned.`（有本机参考音） | 即时层：`card_style_for(act)` → 那档参考音 → 小模型。精做层（Mac 且装好了、正在做“整本提前做好”或给手机做）：3.5 |
| `cloud.` | 现有的 |
| `actor.` | 现有的（调试用） |
| 其余（系统声音） | 现有的 |

`DramaCaps`：即时层装好时 `tts: true`、`tts_same_each_time: false`、`design_voice` 只在 Mac 精做层装好时为 true、`engine` 按第三节的表。

### 7.2 缓存键

现有 `clip_key(voice, text, act, engine)`。声音卡的片段，把 `voice` 换成 `卡 id + "@" + version + "/" + style`（即时层），或 `卡 id + "@" + version`（精做层，语气已经在 `act` 里）。这样重录一张卡，它的片段自然全部作废。

### 7.3 回听（精做层）

照现有的，一处调整：现有的声纹判断是“和这个声音自己各片段的均值比”，带强烈语气的片段声纹会掉到 0.6 上下。改成**按语气档分开记基线**（一张卡每档各一个均值和波动），判“不像”时和同一档比。

### 7.4 导出有声书

对话框里固定一行小字：“合成的声音受各自服务的条款约束，导出的文件供自己听。”不按来源拦。

### 7.5 系统声音

留在声音列表最下面一组“系统自带”。自动用到它只有三种时候：朗读声音还没下好；这门语言小模型不会；手机太热（3.4）。

### 7.6 要先出原型的界面

1. 声音列表的四组（三端）。
2. 朗读面板里“正在准备更好的声音”那一行的三种样子。
3. 克隆：录音页、不合格的提示、试听、卡的详情（加上语气、跟着同步）。
4. Mac 设置朗读页：“精做的声音”一行、“给我的手机提前做好”开关。
5. Mac 菜单栏菜单里“正在给手机做……”一行。

## 八、分步和验收

| 步 | 做什么 | 验收 |
|---|---|---|
| 1 | **试听**（8.1） | 一张表和一个结论：即时层用哪个模型；精做层语气走 3.5 的 a 还是 b |
| 2 | 核心：`drama/card.rs`、2.2 的归档、2.3 的接口、`card_check`、7.2 的键、`drama_section_layer`；全带单元测试 | `cargo test` 过；归档表每一行有测试；指向文件夹外的 `file` 被拒 |
| 3 | `scripts/voice-lab/build_voices.py`，做出 `voices-1.zip`，传 release | 48 张卡全过 4.2 的自动检查；用户听过默认旁白并点头 |
| 4 | 即时层接进三端：`LiveVoiceModel`、`VoicePack`、`CardVoice`、下载；`Player` 的取声音顺序和按时长预合成；热状态 | 新装的 App 第一次朗读：马上出声，下完后下一章换成默认旁白；连念 30 分钟不卡顿 |
| 5 | 广播剧用卡选角；七档语气；轻把关；换层只在章间 | 只有手机：一章里主要人物声音各不相同，生气的台词听得出生气；同一章不变声 |
| 6 | 本机克隆（三端）；旧克隆声音转成卡 | 没有 Key、开飞行模式：录 10 秒，能念书 |
| 7 | Mac 精做层进 App（mlx-audio 的 Swift 包）；照卡念；造卡；7.3 | Mac 上不开终端能“整本提前做好”；同一个人物即时层和精做层盲听是同一个人 |
| 8 | 造的卡跟书同步；`/card`；Mac 当声音工坊 | 手机开书，Mac 自己开始做；手机拿到后下一章起变成精做的 |

每一步做完：三端都编译过；写进 `docs/dev/test-checklist.md` 对应的测试项（照“先实现后测试”，清单由用户说了再跑）；在本文末尾“实现进度”记一笔。

### 8.1 第 1 步怎么试听

做一个 `scripts/voice-lab/audition.py`，在 Mac 上跑，出一个文件夹的 WAV 和一张 `audition.md` 表。不进 App。

**材料**：中文——《背影》全文（旁白为主）、《红楼梦》第八回里 20 句对话；英文——《傲慢与偏见》第一章。参考音：用 Qwen3-TTS VoiceDesign 造 4 张卡（中文男女、英文男女），各七段。

**跑什么**：

| 编号 | 内容 | 看什么 |
|---|---|---|
| A | 三个候选小模型（ZipVoice 蒸馏版、ZONOS3-Mini、Kokoro v1.1-zh）各念三份材料；系统声音也念一遍当对照 | 好听、稳（漏字吞字重复，用听写自动数）、合成一秒声音要几秒 |
| B | 同一张卡的 `neutral`：小模型念 vs Qwen3-TTS Base 念，同 10 句 | 声纹相似度；盲听是不是同一个人 |
| C | 同一句台词，小模型分别照七档参考音念 | 听得出语气不同吗；和 `neutral` 的声纹掉到多少 |
| D | Qwen3-TTS Base 照参考音念时带不带得动说话指令 | 定 3.5 的 a 还是 b |
| E | 候选小模型在一台中档 Android 真机和一台 iPhone 上：合成速度、内存、连念 30 分钟的温度 | 能不能当即时层 |
| F | 读三个模型官方模型卡的许可全文 | 能不能随 GPL 软件分发；有没有非商用限制 |

**过关线**（即时层的模型要全过）：

- 好听：中英文各十句和系统声音盲听，至少八句更好（用户听）。
- 稳：一章里漏字、吞字、重复合计 ≤ 2 处。
- 像：B 的声纹相似度 ≥ 0.7，盲听认作同一个人。
- 语气：C 里七档至少五档听得出来。
- 快：手机上合成比播放快一倍以上（RTF ≤ 0.5）；内存峰值 ≤ 600 MB；连念 30 分钟不到“serious”。
- 许可：允许再分发。只许非商用的可以用（现有的对齐模型就是），但要在 `NOTICE` 和 release 说明里写清。

A–D、F 可以先跑完出表；“好听”那两条和 E 的真机要人来听、来试。

## 九、每一步先读哪些文件

| 要做的 | 先读 |
|---|---|
| 声音卡、归档、键 | `core/yuyue-core/src/drama/mod.rs`（`DramaAct` 等）、`casting.rs`（`VoiceInfo`、`cast`）、`queue.rs`（`ClipAsk`、`clip_key`、`measure`、`judge`）、`library.rs`（Ogg 解码、登记文件夹的写法）、`api.rs`（`DramaCaps`、`DramaTask`、`drama_cast_voices`、存取的写法） |
| 人物的声音设置 | `core/yuyue-core/src/roles.rs`（`RoleVoice`） |
| Apple 声音 | `apple/Yuyue/Speech/Voice.swift`（`Voice`、`Voices.all/find`）、`SystemVoice.swift`、`CloudVoice.swift`、`ActorVoice.swift`、`ClonedVoice.swift`、`RoleVoices.swift` |
| Apple 播放 | `apple/Yuyue/Speech/Player.swift`（`ahead`、`render()`、`voice(for:)`） |
| Apple 广播剧合成 | `apple/Yuyue/Speech/DramaSynth.swift`（`voices(for:)`、`make`、`design`） |
| Apple 下载 | `apple/Yuyue/App/Aligner.swift`、`apple/Yuyue/Speech/SoundLibrary.swift`、`SpeakerPrint.swift` |
| 局域网 | `apple/Yuyue/App/RecordingShare.swift`；Android `speech/RecordingFetch.kt`、`ShareStream.kt` |
| 同步 | `apple/Yuyue/App/Sync.swift`；核心 `store.rs`、`snapshot.rs` |
| Android | `speech/SystemVoice.kt`（`BookVoice`、`Voices`）、`RoleVoices.kt`（`ClonedVoice`、`SampleCheck`）、`CloudVoice.kt`、`DramaSynth.kt`、`Player.kt`、`Aligner.kt`、`SoundLibrary.kt`、`SpeakerPrint.kt` |
| 脚本 | `scripts/actor-server/serve.py`（Qwen3-TTS 三个模型怎么调）、`scripts/sound-lab/build_library.py`、`scripts/upload-models.sh` |
| 编译和启动 | `apple/build-core.sh`、`apple/project.yml`；Android 用本机的 SDK/NDK |

## 十、风险和退路

| 风险 | 退路 |
|---|---|
| 首选小模型中文不过关 | 换 ZONOS3-Mini（要先确认它会中文）；再不行，即时层用 Kokoro v1.1-zh 的固定声音 |
| 退到 Kokoro（不能照参考音念） | 平常朗读照样比系统声音好；自带声音改成 Kokoro 的 100 个中文声音里挑 32 个；“同一个人”“手机带语气”“本机克隆”三样只在 Mac 精做层和它传给手机的片段里有；第 5、6 步在手机上不做，核心的声音卡照做（Mac 用） |
| 小模型照带语气的参考音念，语气带不过去 | 手机只用 `neutral`，分角色不带语气；精做层不受影响 |
| 小模型权重只许非商用 | 可以用，写进 `NOTICE`；不许再分发的不用 |
| 手机上太慢或太烫 | 中低档机不装即时层（按内存 < 6 GB 判），照旧系统声音，声音列表“推荐”组写“这台手机带不动” |
| 带语气后同一张卡不像同一个人 | 4.2 的 0.55 线卡住；过不了的那档从卡里去掉，用 `neutral` |
| mlx-audio 的 Swift 包跑不了 VoiceDesign 或 Base | 这个模型暂时留在 Python 脚本里（调试用），App 里只做它能跑的；造卡这一步先由脚本做好放进声音包 |

## 十一、查过的和没查到的（2026-11-16）

**查到的**

- Qwen3-TTS：模型卡和技术报告写 Apache-2.0；没查到对九个预设音色另加限制。本方案不用预设音色，避开这个问题。
- mlx-audio 的 Swift 包支持 Qwen3-TTS（查到列出的是 0.6B Base 8-bit；1.7B 的三个要在第 7 步确认）。
- Qwen3-TTS 在 Mac 上合成约等于或略快于播放；在 iPhone 17 Pro Max 上克隆要四倍时长——所以手机不跑它。
- ZipVoice 蒸馏量化版约 104 MB，中英文，要配 vocos 24 kHz 声码器；照参考音念要同时给参考音的文字。
- ZONOS3-Mini（2026-11 发）：权重 Apache-2.0，800M 参数、每次只用 100M，为本机实时做的，克隆不用给文字。
- Kokoro v1.1-zh：100 个中文声音（55 女 45 男），固定音色。

**没查到，第 1 步要自己测的**

- 三个小模型的中文到底好不好听——网上只有商家自己的说法。
- ZipVoice 权重的许可（代码是 Apache-2.0；训练语料是非商用的，权重怎么算要看官方模型卡）。
- 小模型在手机上的真实速度、内存和发热。
- 照着带语气的参考音念，语气能带过去几成。
- ZONOS3-Mini 会不会中文。

## 来源

- Qwen3-TTS 模型卡：https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice/blob/main/README.md
- Qwen3-TTS 技术报告：https://arxiv.org/html/2601.15621
- mlx-audio-swift：https://github.com/Blaizzy/mlx-audio-swift
- Qwen3-TTS 在 Apple 芯片上的压缩和实测：https://atomgradient.github.io/swift-qwen3-tts
- iPhone 上跑 Qwen3-TTS 的实测（Echovo）：https://heise.de/-11172097
- ZipVoice 论文：https://arxiv.org/html/2506.13053v3
- sherpa-onnx 的 ZipVoice 页：https://csukuangfj.github.io/sherpa/onnx/tts/zipvoice.html
- ZONOS3-Mini：https://huggingface.co/Zyphra/ZONOS3-Mini
- Kokoro-82M-v1.1-zh：https://huggingface.co/hexgrad/Kokoro-82M-v1.1-zh

## 实现进度

- **2026-11-18 上午**：
  - **7.6 原型**：已完成声音体验原型（覆盖三端声音列表四组、朗读面板“准备更好的声音”三态、本机克隆录音/不合格把关/自动试听/六档语气详情、Mac 设置朗读页“精做的声音”与“给我的手机提前做好”、Mac 菜单栏“正在给手机做……”）。
  - **第 2 步（核心 `drama/card.rs`）**：已完成并验收通过（`CardStyle` 2.2 归档表全部分支、`VoiceCard` Ogg/WAV 解码秒数与越界路径拦截、`cards_set_pack` / `cards_builtin` / `cards_of_book` / `cards_cloned` / `card_save` / `card_delete` / `card_find` / `card_clip_path` / `card_clip_resolved` / `card_decode_clip` / `card_prepare_pcm`、`card_check` 五项录音把关、7.2 带 `@version/style` 的缓存键、7.3 按语气分档声纹基线、3.1 `drama_section_layer` 整章切换与跟书/可选克隆同步；`cargo test` 全绿，Apple XCFramework 与 Android JNI 绑定已重新生成）。
  - **第 3 步脚本**：已完成 `scripts/voice-lab/voices.yaml`（48 张卡定义，严格满足中 32 / 英 16、男女各半、1:3:3:1 年龄比与默认旁白）与 `scripts/voice-lab/build_voices.py`（可中断续做 + 4.2 自动质检打包 `voices-1.zip`）。
  - **第 1 步（试听 `audition.py`）**：已完成客观实测（共 610 段 WAV）：
    - **即时层定案**：`ZipVoice-Distill-Int8` + `vocos_24khz.onnx`（`guidance_scale=3.0, num_steps=3`），纯 `onnxruntime`（关内存池、参考音取 4.5s、单次分句 ≤ 35 字/25 词）4 核 RTF `0.214–0.227`、峰值 RSS `592–602 MB`，《背影》CER `0.44%`、漏吞复读 `0` 处；`ZONOS3-Mini` 因最小 GGUF 1.1 GB 超手机内存红线淘汰，`Kokoro` 因固定音色且 CPU RTF 0.85 留作退路；权重许可 CC-BY-NC 4.0（可再分发，写进 `NOTICE`）。
    - **精做层语气定案**：走 **3.5 b**（`Qwen3-TTS Base` 源码 `_generate_icl` 忽略说话指令，实测照卡上对应语气档参考音克隆 CER 0%、声纹 0.59–0.91）；造卡 7 段全用 `VoiceDesign` + CAM++ ≥ 0.55 把关（4 张卡 28/28 段全过）。
    - 待用户盲听确认 `试听面板.html` 与过目 `声音体验-原型.html` 后推进第 3–8 步端侧接入。
- **2026-11-18 下午（全部 1–8 步完成并三端编译通过）**：
  - **第 2 步补齐（纯数学 ZipVoice 核心 `drama/zipvoice.rs`）**：核心内完成双语扁平竞技场词表加载（内存 < 8 MB，替代 `sherpa-onnx` 的 300 MB 哈希表）、`{词|读音}` 拼音覆盖与简体归一及数字展开、长句按 ≤ 32 字 / 22 词切分、24 kHz 100 维 Kaldi-mel 提取、偏移 Euler 时间步、Box-Muller 噪声、Vocos 1024 点 ISTFT 与 −20 LUFS 归一、3.3 轻把关（`live_voice_check`）；打包 `live-zipvoice-1.zip`（149,776,622 字节，BLAKE3 `7a2542b6301088152211d6edf4bd66ac3527fd29ec85ecf9b35fd4cc059b6bf9`）并更新 `NOTICE` 与 `scripts/upload-models.sh`。
  - **第 3 步（48 张 CC0 自带声音包 `voices-1.zip`）**：完成全部 48 张卡（中文男女各 16、英文男女各 8，少年/青年/中年/老年 1:3:3:1，含 4 张默认旁白：沉舟 `card.b.01`、清禾 `card.b.17`、Clara `card.b.33`、Arthur `card.b.41`）生成与复核：48/48 具备 192 维 `CAM++` 声纹，同语言同性别两两余弦最高 `0.7960 < 0.80`，各档语气对 `neutral` ≥ 0.55、无削波、首词完整无截断（共 292 段 Ogg，打包 `voices-1.zip` 14,002,684 字节，BLAKE3 `2d35635523d08b1b2d9d5cbfb49695a1eb424bbe7d4eafe51e1c255414d44f2c`）。
  - **第 4–6 步（即时层 + 选角语气 + 本机零样本克隆，Apple & Android）**：
    - Apple：`apple/OnnxRuntime`（封装官方 `onnxruntime-c 1.24.2` XCFramework，关闭内存池）、`LiveVoiceModel.swift`、`CardVoice.swift`、`VoicePickerSheet.swift`、`CloneVoiceView.swift`，接通 `Player.swift`（按秒数预合成到 ≥ 15 秒、章间切换默认旁白、`< 6 GB` 低内存保护）与 `DramaSynth.swift`（2.2 七档参考音选档、3.3 轻把关失败自动降级、3.1 整章齐才切层）及旧 `ClonedVoice` 无损迁卡。
    - Android：`onnxruntime-android:1.20.0`、`LiveVoiceModel.kt`、`CardVoice.kt`、`VoiceSheets.kt`，接通 `Player.kt`、`DramaSynth.kt`、`RoleVoices.kt`、`ReaderScreen.kt`、`ListeningPage.kt`、`SettingsPages.kt` 与 `VoiceExperienceTest.kt`（9 项单测全绿）。
  - **第 7–8 步（Mac 内置精做层 + 局域网声音工坊 `/card` & `/want`）**：
    - Mac 接入 `MLXAudio`（`Qwen3-TTS-12Hz-1.7B-Base-6bit` 照卡对应语气档克隆 + `VoiceDesign-6bit` 为缺声音主要人物造七档声音卡 + 7.3 按语气档声纹验收），支持 Mac 设置“朗读 → 精做的声音”一键下载/取消/删除及“给我的手机提前做好”与菜单栏进度。
    - `RecordingShare.swift` 与 Android `RecordingFetch.kt` 补齐 `GET /card?book=…`、`POST /want?book=…&upto=…` 与卡文件拉取，造出的角色卡随书同步，克隆卡默认留本机并提供“同步到我的其他设备”开关。
  - **编译与词表验收**：`cargo test` 全绿；macOS `xcodebuild`、iOS `xcodebuild`、Android `assembleDebug` + `testDebugUnitTest` 全部编译与测试通过；Apple `Localizable.xcstrings` 新增英文词条 0 缺失。2026-11-18 `live-zipvoice-1.zip`、`voices-1.zip` 已传到 `models-1`；真机 30 分钟发热实测待做。

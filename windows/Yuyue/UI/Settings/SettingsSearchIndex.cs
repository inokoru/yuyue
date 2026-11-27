namespace Yuyue.UI.Settings;

public sealed record SettingsSearchEntry(string PageId, string CategoryLabel, string Title, string Subtitle, string Keywords);

/// <summary>
/// Global Settings search index spanning all 11 domains (Chinese &amp; English keywords),
/// powering instant in-window search &amp; jump inside <see cref="SettingsView"/>.
/// </summary>
public static class SettingsSearchIndex
{
    public static readonly IReadOnlyList<SettingsSearchEntry> Entries =
    [
        // 通用
        new("general", "通用", "开书默认新标签页", "单主窗口多标签或独立窗口打开书籍", "tab window 标签 窗口 多标签"),
        new("general", "通用", "启动时继续上次阅读", "重新打开上次未关闭的书籍与分屏", "restore session 启动 恢复 标签"),
        new("general", "通用", "关闭主窗口行为", "听书时最小化到托盘、始终最小化到托盘或直接退出", "close tray minimize 托盘 关闭 后台 退出"),
        new("general", "通用", "书架打开习惯", "单击选中+双击打开（支持多选）或单击直开", "click double select 单击 双击 多选 书架 书库"),
        new("general", "通用", "阅读器空格键行为", "智能空格（静读翻页/听书暂停）或始终控制朗读", "space key smart 空格 翻页 朗读"),
        new("general", "通用", "Windows 快捷键速查", "F11全屏、Ctrl+滚轮缩放、Alt+←/→历史、Ctrl+W/Shift+T", "shortcut hotkey keyboard 快捷键 全屏 缩放"),
        new("general", "通用", "文件格式关联", "将 EPUB / PDF / TXT / MOBI / AZW3 关联到语阅打开", "file association epub pdf txt mobi 文件 关联 默认"),
        new("general", "通用", "开机自动启动", "登录 Windows 时静默常驻系统托盘", "startup login autolaunch 开机 自启"),
        new("general", "通用", "朗读时防止系统休眠", "朗读或播放有声书期间保持系统唤醒", "awake sleep power 休眠 常亮"),
        new("general", "通用", "语言", "跟随系统 / 中文 / English", "language zh en 语言 英文 中文"),
        new("general", "通用", "监听一个文件夹", "放进这个文件夹的书，下次打开语阅时自动导入", "watch folder import 监听 监视 文件夹 导入"),
        new("general", "通用", "Wi‑Fi 传书", "手机或平板浏览器扫码/输入地址传书", "wifi transfer lan 传书 局域网"),

        // 外观
        new("appearance", "外观", "界面", "浅色、深色或跟随 Windows 系统外观", "theme light dark system 主题 浅色 深色 外观"),
        new("appearance", "外观", "Warm Mica 暖云母材质", "开启或关闭 Windows 11 原生云母透光窗口外壳", "mica acrylic backdrop 云母 材质 透明"),
        new("appearance", "外观", "主题色", "暖纸、草木、烟雨、海棠及 Windows 系统蓝", "tint accent color winblue 暖纸 草木 烟雨 海棠 系统蓝 主题色 强调色"),
        new("appearance", "外观", "应用图标", "切换任务栏与窗口图标配色", "icon appicon 图标"),
        new("appearance", "外观", "全局墨水屏护眼模式", "低对比度静态无动画显示", "eink gray 墨水屏 护眼"),

        // 阅读
        new("reading", "阅读", "纸色与自定义纸", "素白、纸本、芦苇、铅纸、暗黑、纸本 · 夜及自定义纸，支持跟随外观", "paper kami sage dark plain follow 纸张 纸色 纸本 芦苇 素白 夜间"),
        new("reading", "阅读", "字体、字号与行距", "调整正文默认字体、字号、行距、段距与导入本地字体", "font size lineheight typography 字体 字号 行距 宋体 楷体"),
        new("reading", "阅读", "翻页方式与宽窗口双栏", "左右翻页、上下滚动与宽窗口下双栏显示", "paginated scroll column 分页 滚动 双栏 单栏"),
        new("reading", "阅读", "沉浸阅读偏好", "关闭翻页动画等沉浸设置", "immersive fullscreen animation 沉浸 动画"),
        new("reading", "阅读", "翻页音效与自定义音色", "6 种仿真翻纸与案头音效，支持音量调节、麦克风实录与导入音频", "page turn sound sfx audio volume custom paper 翻页 音效 音色 音量 自定义 轻纸 脆页 宣纸 精装 轻叩 滴墨"),

        // 外文
        new("foreign", "外文", "点词查词与自动发音", "内置简明英汉词典，支持查词自动发音与英音/美音口音切换", "lookup dictionary english ecdict speak accent uk us 查词 外文 词典 发音 口音 英音 美音"),
        new("foreign", "外文", "难词行间释义与生词标记", "按四级/六级/考研/雅思/GRE 自动显示词义提示", "gloss vocab cet4 cet6 gre 生词 释义 行间"),
        new("foreign", "外文", "MDict 词典", "导入、启用或排序离线 .mdx 词典", "mdict mdx 离线词典"),

        // 朗读
        new("listening", "朗读", "中文书与英文书声音、语速", "选择离线语音、克隆声音、云端声音或系统自带声音", "voice tts kokoro edge speech rate 朗读 音色 语速 倍速 听书"),
        new("listening", "朗读", "语阅离线语音（Kokoro）", "下载 125 MB 离线多音色模型，断网也可流畅听书", "kokoro offline model download 离线模型 下载"),
        new("listening", "朗读", "克隆一个声音", "录制数秒人声生成你的专属朗读声线", "clone voice 克隆 音色"),

        // 书伴
        new("companion", "书伴", "模型服务", "支持 DeepSeek、OpenAI、Claude、Gemini、OpenRouter、Ollama 等 18 家", "ai llm companion deepseek openai claude gemini ollama 书伴 模型 密钥"),
        new("companion", "书伴", "常用提问", "自定义最多 8 条快捷提问与 / 指令名", "prompt quick slash command 常用提问 快捷指令 提问"),
        new("companion", "书伴", "省费与用量", "轻量任务自动走快速模型并统计 30 天用量", "tier budget token usage 路由 用量 限额 省费"),

        // 同步
        new("sync", "同步", "WebDAV / 本地目录 / Google Drive 同步", "坚果云等 WebDAV 账号配置与同步项开关", "sync webdav jianguoyun onedrive gdrive 同步 坚果云 网盘"),
        new("sync", "同步", "同步内容与冲突处理", "选择同步进度和书签、笔记和生词、设置和替换规则或书文件", "conflict selective 冲突 进度 笔记同步"),

        // 笔记导出
        new("export", "笔记导出", "Obsidian / Notion / flomo / 新枝导出", "将划线与想法发送到常用笔记软件", "export obsidian notion flomo xinzhi markdown 导出 笔记"),

        // 存储
        new("storage", "存储", "书库数据位置与已用空间", "查看本地数据目录、电子书体积与模型缓存", "storage disk path folder 存储 空间 目录 缓存"),

        // 数据
        new("data", "数据", "生成与导入 .yuyue 快照", "打包备份全部书、笔记与进度，随时迁移新电脑", "snapshot backup restore export csv 快照 备份 恢复 迁移"),
        new("data", "数据", "最近改过", "查看最近的设置改动并一键改回或还原", "history undo redo 历史 撤销 最近改过"),

        // 关于
        new("about", "关于", "版本、更新和作者", "查看当前版本、更新日志、问题与建议、隐私说明", "about version update changelog feedback privacy 关于 更新 反馈 隐私"),
    ];

    public static IReadOnlyList<SettingsSearchEntry> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        string[] terms = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Entries.Where(e =>
            terms.All(t =>
                e.Title.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                e.Subtitle.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                e.CategoryLabel.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                e.Keywords.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .Take(12)
            .ToList();
    }
}

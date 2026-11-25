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
        // 通用与操控
        new("general", "通用与操控", "开书默认新标签页", "单主窗口多标签或独立窗口打开书籍", "tab window 标签 窗口 多标签"),
        new("general", "通用与操控", "启动时恢复上次打开的标签页", "重新打开上次未关闭的书籍与分屏", "restore session 启动 恢复 标签"),
        new("general", "通用与操控", "关闭主窗口行为", "听书时最小化到托盘、始终最小化到托盘或直接退出", "close tray minimize 托盘 关闭 后台 退出"),
        new("general", "通用与操控", "书库打开习惯", "单击选中+双击打开（支持多选）或单击直开", "click double select 单击 双击 多选 书库"),
        new("general", "通用与操控", "阅读器空格键行为", "智能空格（静读翻页/听书暂停）或始终控制朗读", "space key smart 空格 翻页 朗读"),
        new("general", "通用与操控", "Windows 快捷键速查", "F11全屏、Ctrl+滚轮缩放、Alt+←/→历史、Ctrl+W/Shift+T", "shortcut hotkey keyboard 快捷键 全屏 缩放"),
        new("general", "通用与操控", "文件格式关联", "将 EPUB / PDF / TXT / MOBI / AZW3 关联到语阅打开", "file association epub pdf txt mobi 文件 关联 默认"),
        new("general", "通用与操控", "开机自动启动", "登录 Windows 时静默常驻系统托盘", "startup login autolaunch 开机 自启"),
        new("general", "通用与操控", "界面语言", "跟随系统 / 简体中文 / English", "language zh en 语言 英文 中文"),
        new("general", "通用与操控", "监视文件夹自动导入", "放入指定目录的电子书自动收入书库", "watch folder import 监视 文件夹 导入"),
        new("general", "通用与操控", "局域网 Wi-Fi 传书", "手机或平板浏览器扫码/输入地址传书", "wifi transfer lan 传书 局域网"),

        // 外观与材质
        new("appearance", "外观与材质", "深浅主题模式", "浅色、深色或跟随 Windows 系统外观", "theme light dark system 主题 浅色 深色"),
        new("appearance", "外观与材质", "Warm Mica 暖云母材质", "开启或关闭 Windows 11 原生云母透光窗口外壳", "mica acrylic backdrop 云母 材质 透明"),
        new("appearance", "外观与材质", "界面主题强调色", "暖纸、草木、烟雨、海棠及 Windows 系统蓝", "tint accent color winblue 暖纸 草木 烟雨 海棠 系统蓝 强调色"),
        new("appearance", "外观与材质", "应用图标风格", "切换任务栏与窗口图标配色", "icon appicon 图标"),
        new("appearance", "外观与材质", "纸色跟随外观", "浅色/深色模式切换时自动配对日间与夜间阅读纸张", "paper follow appearance 纸色 自动 夜间"),
        new("appearance", "外观与材质", "全局墨水屏护眼模式", "低对比度静态无动画显示", "eink gray 墨水屏 护眼"),

        // 阅读排版
        new("reading", "阅读排版", "默认阅读纸张与自定义纸", "素白、纸本、芦苇、铅纸、暗黑、纸本·夜及自定义纸", "paper kami sage dark plain 纸张 纸本 芦苇 素白"),
        new("reading", "阅读排版", "字体、字号与行距", "调整正文默认字体、字号、行距、段距与导入本地字体", "font size lineheight typography 字体 字号 行距 宋体 楷体"),
        new("reading", "阅读排版", "分页、滚动与双栏", "默认翻页模式与宽屏双栏排版", "paginated scroll column 分页 滚动 双栏 单栏"),
        new("reading", "阅读排版", "沉浸阅读偏好", "关闭翻页动画、隐藏干扰提示等", "immersive fullscreen animation 沉浸 动画"),

        // 外文与词典
        new("foreign", "外文与词典", "点词查词与自动发音", "内置简明英汉词典及英音/美音朗读", "lookup dictionary english ecdict 查词 外文 词典 发音"),
        new("foreign", "外文与词典", "生词标注与难词行间释义", "按四级/六级/考研/雅思/GRE 自动显示词义提示", "gloss vocab cet4 cet6 gre 生词 释义 行间"),
        new("foreign", "外文与词典", "本地 MDict (.mdx) 词典管理", "导入、启用或排序离线专业词典", "mdict mdx 离线词典"),

        // 朗读与音色
        new("listening", "朗读与音色", "默认中英文朗读音色与倍速", "选择离线 Kokoro、Edge 神经语音或系统音色", "voice tts kokoro edge speech rate 朗读 音色 倍速 听书"),
        new("listening", "朗读与音色", "Kokoro 离线高品质语音模型", "下载 125 MB 离线多音色模型，断网也可流畅听书", "kokoro offline model download 离线模型 下载"),
        new("listening", "朗读与音色", "克隆专属音色", "录制数秒人声生成你的专属朗读声线", "clone voice 克隆 音色"),
        new("listening", "朗读与音色", "听书时阻止电脑休眠", "朗读或播放有声书期间保持系统唤醒", "awake sleep power 休眠 常亮"),

        // AI 书伴
        new("companion", "AI 书伴", "AI 模型与服务商配置", "支持 DeepSeek、OpenAI、Claude、Gemini、OpenRouter、Ollama 等 18 家", "ai llm companion deepseek openai claude gemini ollama 书伴 模型 密钥"),
        new("companion", "AI 书伴", "智能分层路由与用量限额", "轻量任务自动走快速模型并统计 30 天用量", "tier budget token usage 路由 用量 限额"),

        // 多端同步
        new("sync", "多端同步", "WebDAV / 本地目录 / Google Drive 同步", "坚果云等 WebDAV 账号配置与同步项开关", "sync webdav jianguoyun onedrive gdrive 同步 坚果云 网盘"),
        new("sync", "多端同步", "同步内容与冲突处理", "选择同步阅读进度、笔记、设置或书文件", "conflict selective 冲突 进度 笔记同步"),

        // 笔记导出
        new("export", "笔记导出", "Obsidian / Notion / flomo / 新枝导出", "配置自动或一键将本书划线与想法导出到知识库", "export obsidian notion flomo xinzhi markdown 导出 笔记"),

        // 本地存储
        new("storage", "本地存储", "书库数据位置与磁盘占用", "查看本地数据目录、电子书体积与模型缓存", "storage disk path folder 存储 空间 目录 缓存"),

        // 备份与数据
        new("data", "备份与数据", "生成与恢复 .yuyue 完整快照", "打包备份全部书籍、笔记与进度，随时迁移新电脑", "snapshot backup restore export csv 快照 备份 恢复 迁移"),

        // 关于语阅
        new("about", "关于语阅", "版本信息、检查更新与问题反馈", "查看当前版本、更新日志、提交反馈与隐私说明", "about version update changelog feedback privacy 关于 更新 反馈 隐私"),
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

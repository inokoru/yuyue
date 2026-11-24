using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.UI;
using Windows.UI.Text;
using WinRT.Interop;
using Yuyue.Core;
using Yuyue.Design;

namespace Yuyue.Reader;

/// <summary>
/// 1:1 alignment with macOS <c>ReplaceRules.swift</c> (<c>替换规则</c>) and
/// <c>TxtLayout.swift</c> (<c>编码和目录</c>):
/// live 250ms debounced rule preview with strikethrough before/after sample,
/// Legado / JSON replace-rule import, midpoint reordering, 5-second undo,
/// cross-scope banner, TXT re-chaptering reset, and long-chapter anomaly warning &amp; jump.
/// </summary>
public static class BookToolsDialogs
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    private static int ImportReplaceRulesFromJson(LibraryService library, string json, string? targetBookId)
    {
        using var doc = JsonDocument.Parse(json);
        int imported = 0;

        void TrySaveElement(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object) return;
            string? find = null;
            string replace = "";
            bool isRegex = false;
            bool speechOnly = false;
            bool enabled = true;

            foreach (var prop in el.EnumerateObject())
            {
                string key = prop.Name;
                if (key.Equals("pattern", StringComparison.OrdinalIgnoreCase) || key.Equals("find", StringComparison.OrdinalIgnoreCase))
                    find = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                else if (key.Equals("replacement", StringComparison.OrdinalIgnoreCase) || key.Equals("replace", StringComparison.OrdinalIgnoreCase))
                    replace = prop.Value.ValueKind == JsonValueKind.String ? (prop.Value.GetString() ?? "") : "";
                else if (key.Equals("isRegex", StringComparison.OrdinalIgnoreCase) || key.Equals("regex", StringComparison.OrdinalIgnoreCase))
                    isRegex = prop.Value.ValueKind == JsonValueKind.True || (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int rv) && rv != 0);
                else if (key.Equals("speechOnly", StringComparison.OrdinalIgnoreCase) || key.Equals("speech_only", StringComparison.OrdinalIgnoreCase))
                    speechOnly = prop.Value.ValueKind == JsonValueKind.True;
                else if (key.Equals("isEnabled", StringComparison.OrdinalIgnoreCase) || key.Equals("enabled", StringComparison.OrdinalIgnoreCase))
                    enabled = prop.Value.ValueKind != JsonValueKind.False && !(prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int ev) && ev == 0);
            }

            if (string.IsNullOrEmpty(find)) return;
            try
            {
                library.Core.SaveReplaceRule(new ReplaceRule("", targetBookId, find, replace, isRegex, speechOnly, enabled, 0));
                imported++;
            }
            catch { }
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
                TrySaveElement(item);
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (doc.RootElement.TryGetProperty("rules", out var rulesArr) && rulesArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in rulesArr.EnumerateArray())
                    TrySaveElement(item);
            }
            else
            {
                TrySaveElement(doc.RootElement);
            }
        }

        return imported;
    }
    public static async Task<bool> ShowReplaceRulesDialogAsync(
        XamlRoot xamlRoot, LibraryService library, string bookId, string bookTitle, Action? onRulesChanged = null)
    {
        bool anyChanged = false;
        void NotifyRulesChanged()
        {
            anyChanged = true;
            try { onRulesChanged?.Invoke(); } catch { }
        }
        bool scopeBook = true;
        string subMode = "list"; // list | edit | common
        ReplaceRule? editingRule = null;
        List<ReplaceRule> removedRules = [];
        Guid removedToken = Guid.Empty;
        CancellationTokenSource? previewCts = null;

        bool dark = xamlRoot.Content is FrameworkElement fe && ThemeHelper.IsDark(fe.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);

        BookDetail? detail = null;
        try { detail = library.GetBookDetail(bookId); } catch { }

        ContentDialog? dlgRef = null;
        var host = new Grid();

        void TriggerUndoExpiry(Guid token)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                host.DispatcherQueue?.TryEnqueue(() =>
                {
                    if (removedToken == token && removedRules.Count > 0)
                    {
                        removedRules.Clear();
                        if (subMode == "list") RenderHost();
                    }
                });
            });
        }

        void RenderHost()
        {
            previewCts?.Cancel();
            host.Children.Clear();
            if (subMode == "edit" && editingRule is not null)
                host.Children.Add(BuildEditorView(editingRule));
            else if (subMode == "common")
                host.Children.Add(BuildCommonView());
            else
                host.Children.Add(BuildListView());
        }

        UIElement BuildListView()
        {
            ReplaceRule[] mine;
            ReplaceRule[] shared;
            try { mine = library.Core.ReplaceRules(bookId); } catch { mine = []; }
            try { shared = library.Core.ReplaceRules(null); } catch { shared = []; }
            var rules = scopeBook ? mine : shared;

            var root = new StackPanel { Spacing = 12, Padding = new Thickness(22, 18, 22, 18) };
            root.Children.Add(YYChrome.MakeSheetHeader("替换规则", bookTitle, () => dlgRef?.Hide(), dark, titleSize: 18));

            // Segmented scope bar with counts (1:1 reader-rule-edit/3.png: "这本书 0 | 全部书 0")
            var segGrid = new Grid { ColumnSpacing = 3 };
            segGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            segGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Button MakeScopeSeg(int col, bool isBook, string label, int count)
            {
                bool on = scopeBook == isBook;
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(new TextBlock { Text = L10n.T(label), FontSize = 12.5, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2) });
                sp.Children.Add(new TextBlock { Text = count.ToString(), FontSize = 12, Foreground = ThemeHelper.Brush(p.T3) });
                var b = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Height = 30,
                    MinHeight = 30,
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(0),
                    Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                    UseSystemFocusVisuals = false,
                    Content = sp,
                };
                b.Click += (_, _) => { scopeBook = isBook; RenderHost(); };
                Grid.SetColumn(b, col);
                return b;
            }
            segGrid.Children.Add(MakeScopeSeg(0, true, "这本书", mine.Length));
            segGrid.Children.Add(MakeScopeSeg(1, false, "全部书", shared.Length));
            root.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(p.Inset),
                Padding = new Thickness(3),
                Child = segGrid,
            });

            // Group card
            var groupStack = new StackPanel { Spacing = 0 };
            if (rules.Length == 0)
            {
                groupStack.Children.Add(new TextBlock
                {
                    Text = scopeBook ? "这本书还没有自己的规则" : "还没有用于全部书的规则",
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(12, 14, 12, 14),
                });
            }

            for (int i = 0; i < rules.Length; i++)
            {
                if (i > 0)
                    groupStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });

                int idx = i;
                var rule = rules[i];
                var row = new Grid { ColumnSpacing = 6, Padding = new Thickness(12, 9, 10, 9) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var infoStack = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
                infoStack.Children.Add(new TextBlock
                {
                    Text = rule.Find,
                    FontFamily = new FontFamily(rule.Regex ? YYFontFamily.Mono.WindowsFamily() : YYFontFamily.Sans.WindowsFamily()),
                    FontSize = rule.Regex ? 11.5 : 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(rule.Enabled ? p.T1 : p.T3),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

                var subLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                subLine.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(rule.Replace) ? "→ 删掉" : $"→ {rule.Replace}",
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(rule.Enabled ? p.T2 : p.T3),
                    MaxWidth = 170,
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                if (rule.Regex)
                {
                    subLine.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Background = ThemeHelper.Brush(p.Inset),
                        Padding = new Thickness(5, 1, 5, 1),
                        Child = new TextBlock { Text = "正则", FontSize = 9.5, Foreground = ThemeHelper.Brush(p.T2) },
                    });
                }
                if (rule.SpeechOnly)
                {
                    subLine.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Background = ThemeHelper.Brush(p.Inset),
                        Padding = new Thickness(5, 1, 5, 1),
                        Child = new TextBlock { Text = "只改朗读", FontSize = 9.5, Foreground = ThemeHelper.Brush(p.T2) },
                    });
                }
                infoStack.Children.Add(subLine);

                var editBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    UseSystemFocusVisuals = false,
                    Content = infoStack,
                };
                editBtn.Click += (_, _) =>
                {
                    editingRule = rule;
                    subMode = "edit";
                    RenderHost();
                };
                row.Children.Add(editBtn);

                // Reorder up / down buttons (1:1 ReplaceRules.swift:268-282 midpoint order)
                if (rules.Length > 1)
                {
                    var orderBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                    var upBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "↑", Width = 22, Height = 24, FontSize = 11, IsEnabled = idx > 0 };
                    upBtn.Click += (_, _) =>
                    {
                        MoveRule(rules, idx, idx - 1);
                        NotifyRulesChanged();
                        RenderHost();
                    };
                    var downBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "↓", Width = 22, Height = 24, FontSize = 11, IsEnabled = idx < rules.Length - 1 };
                    downBtn.Click += (_, _) =>
                    {
                        MoveRule(rules, idx, idx + 1);
                        NotifyRulesChanged();
                        RenderHost();
                    };
                    orderBtns.Children.Add(upBtn);
                    orderBtns.Children.Add(downBtn);
                    Grid.SetColumn(orderBtns, 1);
                    row.Children.Add(orderBtns);
                }

                var sw = new ToggleSwitch { IsOn = rule.Enabled, MinWidth = 0, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
                sw.Toggled += (_, _) =>
                {
                    try { library.Core.SetReplaceRuleEnabled(rule.Id, sw.IsOn); NotifyRulesChanged(); RenderHost(); } catch { }
                };
                Grid.SetColumn(sw, 2);
                row.Children.Add(sw);

                var delBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYIconButtonStyle"],
                    Content = "✕",
                    Width = 24,
                    Height = 24,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                delBtn.Click += (_, _) =>
                {
                    try
                    {
                        library.Core.DeleteReplaceRule(rule.Id);
                        NotifyRulesChanged();
                        removedRules = [rule];
                        removedToken = Guid.NewGuid();
                        TriggerUndoExpiry(removedToken);
                        RenderHost();
                    }
                    catch { }
                };
                Grid.SetColumn(delBtn, 3);
                row.Children.Add(delBtn);

                var ctx = new MenuFlyout();
                var editMi = new MenuFlyoutItem { Text = "编辑" };
                editMi.Click += (_, _) => { editingRule = rule; subMode = "edit"; RenderHost(); };
                var delMi = new MenuFlyoutItem { Text = "删掉", Foreground = ThemeHelper.Brush(p.Danger) };
                delMi.Click += (_, _) =>
                {
                    try
                    {
                        library.Core.DeleteReplaceRule(rule.Id);
                        NotifyRulesChanged();
                        removedRules = [rule];
                        removedToken = Guid.NewGuid();
                        TriggerUndoExpiry(removedToken);
                        RenderHost();
                    }
                    catch { }
                };
                ctx.Items.Add(editMi);
                ctx.Items.Add(delMi);
                row.ContextFlyout = ctx;

                groupStack.Children.Add(row);
            }

            // 5s inline undo row (1:1 ReplaceRules.swift:238-265)
            if (removedRules.Count > 0)
            {
                groupStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });
                var undoGrid = new Grid { Padding = new Thickness(12, 8, 12, 8), ColumnSpacing = 8 };
                undoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                undoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                string msg = removedRules.Count == 1
                    ? $"删掉了“{removedRules[0].Find} → {(string.IsNullOrEmpty(removedRules[0].Replace) ? "删掉" : removedRules[0].Replace)}”"
                    : $"删掉了 {removedRules.Count} 条规则";
                undoGrid.Children.Add(new TextBlock { Text = msg, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                var undoBtn = new Button
                {
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    Content = "撤销",
                };
                undoBtn.Click += (_, _) =>
                {
                    foreach (var r in removedRules)
                    {
                        try { library.Core.RestoreReplaceRule(r.Id); } catch { }
                    }
                    removedRules.Clear();
                    NotifyRulesChanged();
                    RenderHost();
                };
                Grid.SetColumn(undoBtn, 1);
                undoGrid.Children.Add(undoBtn);
                groupStack.Children.Add(undoGrid);
            }

            // + 添加一条规则
            groupStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });
            var addRuleBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 9, 12, 9),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = ThemeHelper.Brush(tint.Accent),
                UseSystemFocusVisuals = false,
                Content = "＋ 添加一条规则",
            };
            addRuleBtn.Click += (_, _) =>
            {
                editingRule = new ReplaceRule("", scopeBook ? bookId : null, "", "", false, false, true, 0);
                subMode = "edit";
                RenderHost();
            };
            groupStack.Children.Add(addRuleBtn);

            var listScroll = new ScrollViewer
            {
                MaxHeight = 290,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(1),
                    Child = groupStack,
                },
            };
            root.Children.Add(listScroll);

            // 1:1 reader-rule-edit/3.png: clean note + bottom bar (+ 常用规则 on left, 好了 on right)
            if (scopeBook && shared.Length > 0)
            {
                var crossBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(12, 9, 12, 9),
                    CornerRadius = new CornerRadius(11),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    UseSystemFocusVisuals = false,
                };
                var xg = new Grid();
                xg.Children.Add(new TextBlock { Text = $"全部书的 {shared.Length} 条也在生效", FontSize = 12, Foreground = ThemeHelper.Brush(p.T1) });
                xg.Children.Add(new TextBlock { Text = "看看 ›", FontSize = 11, Foreground = ThemeHelper.Brush(tint.Accent), HorizontalAlignment = HorizontalAlignment.Right });
                crossBtn.Content = xg;
                crossBtn.Click += (_, _) => { scopeBook = false; RenderHost(); };
                root.Children.Add(crossBtn);
            }

            root.Children.Add(new TextBlock
            {
                Text = "规则从上往下依次执行，按住一条可以拖动顺序。只改朗读的规则不动屏幕上的字，只改读出来的音。",
                FontSize = 11.5,
                LineHeight = 18,
                Foreground = ThemeHelper.Brush(p.T3),
                TextWrapping = TextWrapping.Wrap,
            });

            var importNoticeTb = new TextBlock
            {
                FontSize = 11,
                Foreground = ThemeHelper.Brush(tint.Accent),
                Visibility = Visibility.Collapsed,
                TextWrapping = TextWrapping.Wrap,
            };
            root.Children.Add(importNoticeTb);

            var footRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            var leftActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
            var commonPill = YYChrome.MakeSheetPillButton(L10n.T("＋  常用规则"), () => { subMode = "common"; RenderHost(); }, dark, library.Prefs.Tint, "secondary", height: 36, horizontalPadding: 14);
            var importJsonPill = YYChrome.MakeSheetPillButton(L10n.T("导入 JSON…"), async () =>
            {
                try
                {
                    var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
                    picker.FileTypeFilter.Add(".json");
                    picker.FileTypeFilter.Add(".txt");
                    IntPtr hwnd = GetActiveWindow();
                    if (hwnd != IntPtr.Zero)
                        InitializeWithWindow.Initialize(picker, hwnd);
                    var file = await picker.PickSingleFileAsync();
                    if (file is null) return;
                    string raw = await File.ReadAllTextAsync(file.Path);
                    int count = ImportReplaceRulesFromJson(library, raw, scopeBook ? bookId : null);
                    if (count > 0)
                    {
                        NotifyRulesChanged();
                        RenderHost();
                    }
                    else
                    {
                        importNoticeTb.Text = L10n.T("未在所选文件中识别到可用的替换规则（支持阅读 Legado 净化规则 JSON）。");
                        importNoticeTb.Visibility = Visibility.Visible;
                    }
                }
                catch (Exception ex)
                {
                    importNoticeTb.Text = $"{L10n.T("导入失败：")}{ex.Message}";
                    importNoticeTb.Visibility = Visibility.Visible;
                }
            }, dark, library.Prefs.Tint, "secondary", height: 36, horizontalPadding: 14);
            leftActs.Children.Add(commonPill);
            leftActs.Children.Add(importJsonPill);

            var donePill = YYChrome.MakeSheetPillButton(L10n.T("好了"), () => dlgRef?.Hide(), dark, library.Prefs.Tint, "warm", height: 36, horizontalPadding: 20);
            donePill.HorizontalAlignment = HorizontalAlignment.Right;
            footRow.Children.Add(leftActs);
            footRow.Children.Add(donePill);
            root.Children.Add(footRow);

            return root;
        }

        void MoveRule(ReplaceRule[] current, int fromIdx, int toIdx)
        {
            if (toIdx < 0 || toIdx >= current.Length || fromIdx == toIdx) return;
            var list = current.ToList();
            var moved = list[fromIdx];
            list.RemoveAt(fromIdx);
            list.Insert(toIdx, moved);

            double? before = toIdx > 0 ? list[toIdx - 1].Order : null;
            double? after = toIdx < list.Count - 1 ? list[toIdx + 1].Order : null;
            double newOrder = (before, after) switch
            {
                (double b, double a) => (b + a) / 2.0,
                (double b, null) => b + 1.0,
                (null, double a) => a - 1.0,
                _ => moved.Order,
            };
            try { library.Core.MoveReplaceRule(moved.Id, newOrder); } catch { }
        }

        // ---------- Sub-view: ReplaceRuleEditor with 250ms live preview ----------
        UIElement BuildEditorView(ReplaceRule initial)
        {
            bool isNew = string.IsNullOrEmpty(initial.Id);
            var draft = initial;
            var root = new StackPanel { Spacing = 10, Padding = new Thickness(22, 18, 22, 18) };
            root.Children.Add(YYChrome.MakeSheetHeader(isNew ? "添加规则" : "编辑规则", bookTitle, () => { subMode = "list"; RenderHost(); }, dark, titleSize: 18));

            var findBox = new TextBox
            {
                Header = "查找",
                Text = draft.Find,
                PlaceholderText = "要换掉的字",
                FontFamily = new FontFamily(draft.Regex ? YYFontFamily.Mono.WindowsFamily() : YYFontFamily.Sans.WindowsFamily()),
            };
            var replaceBox = new TextBox
            {
                Header = "替换为",
                Text = draft.Replace,
                PlaceholderText = "留空就是删掉",
                FontFamily = new FontFamily(draft.Regex ? YYFontFamily.Mono.WindowsFamily() : YYFontFamily.Sans.WindowsFamily()),
            };
            root.Children.Add(findBox);
            root.Children.Add(replaceBox);

            var optStack = new StackPanel { Spacing = 8 };
            var regexRow = new Grid();
            regexRow.Children.Add(new TextBlock { Text = "正则表达式", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeHelper.Brush(p.T1) });
            var regexSw = new ToggleSwitch { IsOn = draft.Regex, MinWidth = 0, OnContent = "", OffContent = "", HorizontalAlignment = HorizontalAlignment.Right };
            regexRow.Children.Add(regexSw);
            optStack.Children.Add(regexRow);

            var whereRow = new Grid();
            whereRow.Children.Add(new TextBlock { Text = "改哪里", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeHelper.Brush(p.T1) });
            var whereBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
            var bothBtn = new Button { Content = "显示和朗读", Height = 25, MinHeight = 25, FontSize = 11 };
            var speechBtn = new Button { Content = "只改朗读", Height = 25, MinHeight = 25, FontSize = 11 };
            whereBtns.Children.Add(bothBtn);
            whereBtns.Children.Add(speechBtn);
            whereRow.Children.Add(whereBtns);
            optStack.Children.Add(whereRow);

            var scopeRow = new Grid();
            scopeRow.Children.Add(new TextBlock { Text = "用于", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeHelper.Brush(p.T1) });
            var scopeBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
            var thisBookBtn = new Button { Content = "这本书", Height = 25, MinHeight = 25, FontSize = 11 };
            var allBookBtn = new Button { Content = "全部书", Height = 25, MinHeight = 25, FontSize = 11 };
            scopeBtns.Children.Add(thisBookBtn);
            scopeBtns.Children.Add(allBookBtn);
            scopeRow.Children.Add(scopeBtns);
            optStack.Children.Add(scopeRow);

            root.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 8, 12, 8),
                Child = optStack,
            });

            // Live Preview Card (1:1 ReplaceRules.swift:520-578)
            var previewHost = new StackPanel { Spacing = 6 };
            root.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 10),
                Child = previewHost,
            });

            var errTb = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(p.Danger), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
            root.Children.Add(errTb);

            var saveBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 34,
                Content = "保存",
            };
            root.Children.Add(saveBtn);

            if (!isNew)
            {
                var delBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Foreground = ThemeHelper.Brush(p.Danger),
                    FontSize = 11.5,
                    Content = "删掉这条规则",
                };
                delBtn.Click += (_, _) =>
                {
                    try
                    {
                        library.Core.DeleteReplaceRule(draft.Id);
                        NotifyRulesChanged();
                        removedRules = [draft];
                        removedToken = Guid.NewGuid();
                        TriggerUndoExpiry(removedToken);
                        subMode = "list";
                        RenderHost();
                    }
                    catch (Exception ex) { errTb.Text = ex.Message; errTb.Visibility = Visibility.Visible; }
                };
                root.Children.Add(delBtn);
            }

            void RefreshOptionPills()
            {
                bothBtn.Style = (Style)Application.Current.Resources[!draft.SpeechOnly ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"];
                speechBtn.Style = (Style)Application.Current.Resources[draft.SpeechOnly ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"];
                thisBookBtn.Style = (Style)Application.Current.Resources[draft.Book is not null ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"];
                allBookBtn.Style = (Style)Application.Current.Resources[draft.Book is null ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"];
                findBox.FontFamily = new FontFamily(draft.Regex ? YYFontFamily.Mono.WindowsFamily() : YYFontFamily.Sans.WindowsFamily());
                replaceBox.FontFamily = new FontFamily(draft.Regex ? YYFontFamily.Mono.WindowsFamily() : YYFontFamily.Sans.WindowsFamily());
            }

            void RenderPreview(RulePreview? pv)
            {
                previewHost.Children.Clear();
                saveBtn.IsEnabled = !string.IsNullOrEmpty(draft.Find) && string.IsNullOrEmpty(pv?.Error);

                var top = new Grid();
                if (!string.IsNullOrEmpty(pv?.Error))
                {
                    top.Children.Add(new TextBlock { Text = pv.Error, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.Danger), TextWrapping = TextWrapping.Wrap });
                }
                else if (pv is not null && !string.IsNullOrEmpty(draft.Find))
                {
                    string cntText = pv.Count == 0 ? "这本书里没有找到" : $"这本书里有 {pv.Count}{(pv.More ? "+" : "")} 处";
                    top.Children.Add(new TextBlock { Text = cntText, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                    if (pv.First is RuleSample fst)
                    {
                        string? chap = fst.Section < (detail?.Sections.Length ?? 0) ? detail!.Sections[fst.Section].Title : null;
                        string loc = string.IsNullOrWhiteSpace(chap) ? "第 1 处" : $"第 1 处 · {chap}";
                        top.Children.Add(new TextBlock { Text = loc, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 200, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    }
                }
                else
                {
                    top.Children.Add(new TextBlock { Text = "预览", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                }
                previewHost.Children.Add(top);

                if (pv?.First is RuleSample sample && string.IsNullOrEmpty(pv.Error))
                {
                    Grid MakeSampleRow(string label, bool isBefore)
                    {
                        var g = new Grid { ColumnSpacing = 8 };
                        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        g.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(0, 2, 0, 0) });

                        var tb = new TextBlock
                        {
                            FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                            FontSize = 12,
                            Foreground = ThemeHelper.Brush(p.T1),
                            TextWrapping = TextWrapping.Wrap,
                            LineHeight = 19,
                        };
                        if (!string.IsNullOrEmpty(sample.Lead)) tb.Inlines.Add(new Run { Text = sample.Lead });
                        if (isBefore)
                        {
                            tb.Inlines.Add(new Run
                            {
                                Text = sample.Matched,
                                Foreground = ThemeHelper.Brush(p.Danger),
                                TextDecorations = TextDecorations.Strikethrough,
                                FontWeight = FontWeights.SemiBold,
                            });
                        }
                        else
                        {
                            tb.Inlines.Add(new Run
                            {
                                Text = string.IsNullOrEmpty(sample.Replaced) ? "（已删）" : sample.Replaced,
                                Foreground = ThemeHelper.Brush(tint.Accent),
                                FontWeight = FontWeights.SemiBold,
                            });
                        }
                        if (!string.IsNullOrEmpty(sample.Tail)) tb.Inlines.Add(new Run { Text = sample.Tail });

                        Grid.SetColumn(tb, 1);
                        g.Children.Add(tb);
                        return g;
                    }

                    previewHost.Children.Add(MakeSampleRow("改前", isBefore: true));
                    previewHost.Children.Add(MakeSampleRow("改后", isBefore: false));
                    if (draft.SpeechOnly)
                    {
                        previewHost.Children.Add(new TextBlock
                        {
                            Text = "屏幕上还是改前的样子，只有朗读按改后的读",
                            FontSize = 10.5,
                            Foreground = ThemeHelper.Brush(p.T3),
                        });
                    }
                }
                else if (string.IsNullOrEmpty(pv?.Error))
                {
                    previewHost.Children.Add(new TextBlock
                    {
                        Text = string.IsNullOrEmpty(draft.Find) ? "写好要查找的字，这里马上显示改前改后" : "没有可以预览的地方",
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(p.T3),
                    });
                }
            }

            void SchedulePreview(bool immediate = false)
            {
                previewCts?.Cancel();
                var cts = new CancellationTokenSource();
                previewCts = cts;
                var snap = draft;
                _ = Task.Run(async () =>
                {
                    if (!immediate)
                    {
                        try { await Task.Delay(250, cts.Token); } catch { return; }
                    }
                    if (cts.IsCancellationRequested) return;
                    RulePreview? res = null;
                    if (!string.IsNullOrEmpty(snap.Find))
                    {
                        try { res = library.Core.PreviewReplaceRule(bookId, snap); } catch { }
                    }
                    host.DispatcherQueue?.TryEnqueue(() =>
                    {
                        if (!cts.IsCancellationRequested) RenderPreview(res);
                    });
                });
            }

            findBox.TextChanged += (_, _) => { draft = draft with { Find = findBox.Text }; SchedulePreview(); };
            replaceBox.TextChanged += (_, _) => { draft = draft with { Replace = replaceBox.Text }; SchedulePreview(); };
            regexSw.Toggled += (_, _) => { draft = draft with { Regex = regexSw.IsOn }; RefreshOptionPills(); SchedulePreview(immediate: true); };
            bothBtn.Click += (_, _) => { draft = draft with { SpeechOnly = false }; RefreshOptionPills(); SchedulePreview(immediate: true); };
            speechBtn.Click += (_, _) => { draft = draft with { SpeechOnly = true }; RefreshOptionPills(); SchedulePreview(immediate: true); };
            thisBookBtn.Click += (_, _) => { draft = draft with { Book = bookId }; RefreshOptionPills(); };
            allBookBtn.Click += (_, _) => { draft = draft with { Book = null }; RefreshOptionPills(); };

            saveBtn.Click += (_, _) =>
            {
                if (string.IsNullOrEmpty(draft.Find)) return;
                try
                {
                    var saved = library.Core.SaveReplaceRule(draft);
                    scopeBook = saved.Book is not null;
                    NotifyRulesChanged();
                    subMode = "list";
                    RenderHost();
                }
                catch (CoreException.Invalid inv) { errTb.Text = inv.detail; errTb.Visibility = Visibility.Visible; }
                catch (Exception ex) { errTb.Text = ex.Message; errTb.Visibility = Visibility.Visible; }
            };

            RefreshOptionPills();
            RenderPreview(null);
            if (!string.IsNullOrEmpty(draft.Find)) SchedulePreview(immediate: true);

            return root;
        }

        // ---------- Sub-view: Common Presets (1:1 reader-rules-common/1.png) ----------
        UIElement BuildCommonView()
        {
            ReplaceRule[] existing;
            try { existing = library.Core.ReplaceRules(scopeBook ? bookId : null); } catch { existing = []; }

            var root = new StackPanel { Spacing = 12, Padding = new Thickness(22, 18, 22, 18) };
            root.Children.Add(YYChrome.MakeSheetHeader(
                scopeBook ? "加到这本书" : "加到全部书",
                scopeBook ? bookTitle : null,
                () => { subMode = "list"; RenderHost(); },
                dark,
                titleSize: 18));

            var group = new StackPanel { Spacing = 0 };
            CommonRule[] presets;
            try { presets = library.Core.CommonReplaceRules(); } catch { presets = []; }

            for (int i = 0; i < presets.Length; i++)
            {
                if (i > 0) group.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) });
                var c = presets[i];
                bool added = existing.Any(r => r.Find == c.Rule.Find && r.Regex == c.Rule.Regex);

                var row = new Grid { Padding = new Thickness(14, 11, 14, 11), ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var meta = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                meta.Children.Add(new TextBlock { Text = c.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                meta.Children.Add(new TextBlock { Text = c.Detail, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
                row.Children.Add(meta);

                var addBtn = new Button
                {
                    Height = 28,
                    MinHeight = 28,
                    Padding = new Thickness(12, 0, 12, 0),
                    CornerRadius = new CornerRadius(14),
                    Background = ThemeHelper.Brush(added ? p.Inset : p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(added ? 0 : 0.75),
                    Foreground = ThemeHelper.Brush(added ? p.T3 : tint.Accent),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11.5,
                    IsEnabled = !added,
                    VerticalAlignment = VerticalAlignment.Center,
                    UseSystemFocusVisuals = false,
                    Content = added ? "✓ 已加上" : "＋ 加上",
                };
                addBtn.Click += (_, _) =>
                {
                    try
                    {
                        library.Core.SaveReplaceRule(c.Rule with { Id = "", Book = scopeBook ? bookId : null, Enabled = true });
                        NotifyRulesChanged();
                        RenderHost();
                    }
                    catch { }
                };
                Grid.SetColumn(addBtn, 1);
                row.Children.Add(addBtn);
                group.Children.Add(row);
            }

            root.Children.Add(new ScrollViewer
            {
                MaxHeight = 360,
                Content = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    Child = group,
                },
            });

            var footRow = new Grid { Margin = new Thickness(2, 2, 0, 0) };
            footRow.Children.Add(new TextBlock { Text = "加完还能改、能关", FontSize = 12, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
            var okBtn = YYChrome.MakeSheetPillButton("好了", () => { subMode = "list"; RenderHost(); }, dark, library.Prefs.Tint, "warm", height: 36, horizontalPadding: 20);
            okBtn.HorizontalAlignment = HorizontalAlignment.Right;
            footRow.Children.Add(okBtn);
            root.Children.Add(footRow);
            return root;
        }

        RenderHost();

        dlgRef = YYChrome.CreateMacSheet(xamlRoot, dark, 472, host, tint: library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(library.Prefs.Tint);
        previewCts?.Cancel();
        return anyChanged;
    }

    // ---------- 2. 编码和目录 (1:1 TxtLayout.swift) ----------

    public static async Task<bool> ShowTxtLayoutDialogAsync(
        XamlRoot xamlRoot,
        LibraryService library,
        string bookId,
        string bookTitle,
        Action<uint>? onJumpToSection = null,
        Action? beforeRebuild = null)
    {
        TxtChoice applied;
        try { applied = library.Core.TxtChoice(bookId); }
        catch { applied = new TxtChoice(null, null); }

        string? selectedEnc = applied.Encoding;
        string ruleText = applied.Rule ?? "";
        TxtLayout? currentLayout = null;
        string? readFailure = null;
        bool showAllChapters = false;
        bool working = false;
        uint? pendingJumpSection = null;
        CancellationTokenSource? debounceCts = null;

        bool dark = xamlRoot.Content is FrameworkElement fe && ThemeHelper.IsDark(fe.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);

        ContentDialog? dlgRef = null;
        bool confirmedSave = false;
        var host = new Grid();
        var headerSubtitleTb = new TextBlock
        {
            Text = $"{bookTitle}.txt",
            FontSize = 13,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 1, 0, 0),
        };

        bool IsChoiceChanged()
        {
            string? r = string.IsNullOrWhiteSpace(ruleText) ? null : ruleText.Trim();
            return !string.Equals(selectedEnc, applied.Encoding, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(r, applied.Rule, StringComparison.Ordinal);
        }

        static string FormatWan(uint n)
        {
            double w = n / 10_000.0;
            int tenths = (int)Math.Round(w * 10.0);
            return (w >= 2.0 || tenths % 10 == 0) ? $"{(int)Math.Round(w)} 万" : $"{tenths / 10}.{tenths % 10} 万";
        }

        static string FormatRoughly(uint n)
        {
            if (n >= 10_000) return FormatWan(n);
            if (n >= 1_000) return $"{(int)Math.Round(n / 1_000.0)} 千";
            return $"{(int)Math.Round(n / 100.0) * 100} ";
        }

        static string FormatBytesShort(ulong b)
        {
            if (b >= 1024 * 1024) return $"{b / (1024.0 * 1024.0):0.1} MB";
            if (b >= 1024) return $"{b / 1024.0:0} KB";
            return $"{b} B";
        }

        void RefreshLayout(bool debounce)
        {
            debounceCts?.Cancel();
            var cts = new CancellationTokenSource();
            debounceCts = cts;
            string? enc = selectedEnc;
            string? r = string.IsNullOrWhiteSpace(ruleText) ? null : ruleText.Trim();

            _ = Task.Run(async () =>
            {
                if (debounce)
                {
                    try { await Task.Delay(250, cts.Token); } catch { return; }
                }
                if (cts.IsCancellationRequested) return;
                TxtLayout? found = null;
                string? err = null;
                try { found = library.Core.TxtLayout(bookId, new TxtChoice(enc, r)); }
                catch (Exception ex) { err = ex.Message; }

                host.DispatcherQueue?.TryEnqueue(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    currentLayout = found;
                    readFailure = err;
                    if (!showAllChapters) RenderMainContent();
                });
            });
        }

        var ruleBox = new TextBox
        {
            Text = ruleText,
            FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()),
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 4, 0, 4),
        };
        YYChrome.StripTextBoxChrome(ruleBox);
        var encSegGrid = new Grid { ColumnSpacing = 3 };
        for (int c = 0; c < 4; c++) encSegGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var encHost = new Border
        {
            CornerRadius = new CornerRadius(19),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            Padding = new Thickness(3),
            Child = encSegGrid,
        };
        Button? saveSheetBtn = null;
        var encTrailTb = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right };
        var chapTrailTb = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right };
        var previewCardHost = new StackPanel { Spacing = 4 };
        var unreadableTb = new TextBlock { FontSize = 11, Foreground = ThemeHelper.Brush(p.Danger), Visibility = Visibility.Collapsed };
        var chaptersHost = new StackPanel { Spacing = 0 };
        var resetRuleBtn = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2, 4, 2),
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeHelper.Brush(tint.Accent),
            Content = "用回自动识别",
            Visibility = Visibility.Collapsed,
        };
        var warningHost = new ContentControl();

        ruleBox.TextChanged += (_, _) =>
        {
            ruleText = ruleBox.Text;
            resetRuleBtn.Visibility = !string.IsNullOrWhiteSpace(ruleText) ? Visibility.Visible : Visibility.Collapsed;
            RefreshLayout(debounce: true);
        };
        resetRuleBtn.Click += (_, _) =>
        {
            ruleBox.Text = "";
            ruleText = "";
            resetRuleBtn.Visibility = Visibility.Collapsed;
            RefreshLayout(debounce: false);
        };

        Grid MakeChapterRow(TxtChapter c, bool isLong)
        {
            var g = new Grid { Height = 32, Padding = new Thickness(12, 0, 12, 0), ColumnSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock
            {
                Text = c.Title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T1),
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var charsTb = new TextBlock
            {
                Text = $"{c.Chars:N0} 字",
                FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()),
                FontSize = 11,
                FontWeight = isLong ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ThemeHelper.Brush(isLong ? p.Warn : p.T3),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(charsTb, 1);
            g.Children.Add(charsTb);
            return g;
        }

        void RenderAllChaptersView()
        {
            if (currentLayout is null) return;
            host.Children.Clear();
            var sp = new StackPanel { Spacing = 10, Padding = new Thickness(22, 18, 22, 18) };
            sp.Children.Add(YYChrome.MakeSheetHeader($"全部 {currentLayout.Chapters.Length} 章", bookTitle, () =>
            {
                showAllChapters = false;
                BuildMainFrame();
                RenderMainContent();
            }, dark, titleSize: 18));

            var allStack = new StackPanel { Spacing = 0 };
            int? longIdx = currentLayout.Long is uint l ? (int)l : null;
            for (int i = 0; i < currentLayout.Chapters.Length; i++)
            {
                if (i > 0) allStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });
                allStack.Children.Add(MakeChapterRow(currentLayout.Chapters[i], longIdx == i));
            }
            sp.Children.Add(new ScrollViewer
            {
                MaxHeight = 380,
                Content = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    Child = allStack,
                },
            });
            host.Children.Add(sp);
        }

        void RenderMainContent()
        {
            string sub = currentLayout is not null ? $" · {FormatBytesShort(currentLayout.Size)}" : "";
            headerSubtitleTb.Text = $"{bookTitle}.txt{sub}";
            if (saveSheetBtn is not null)
                saveSheetBtn.IsEnabled = string.IsNullOrEmpty(currentLayout?.Error);

            encTrailTb.Text = currentLayout is not null ? $"自动识别为 {currentLayout.Detected}" : "";
            chapTrailTb.Text = currentLayout is not null && string.IsNullOrEmpty(currentLayout.Error)
                ? $"识别出 {currentLayout.Chapters.Length} 章"
                : "";
            if (currentLayout is not null)
                ruleBox.PlaceholderText = currentLayout.DefaultRule;

            // 1:1 reader-txt/1.png: 4-segment white capsule with warm active pill ("自动 · GBK", "UTF-8", "GB18030", "Big5")
            encSegGrid.Children.Clear();
            string autoLabel = currentLayout is not null ? $"自动 · {currentLayout.Detected}" : "自动";
            var encItems = new (string? Id, string Label)[] { (null, autoLabel), ("UTF-8", "UTF-8"), ("GB18030", "GB18030"), ("Big5", "Big5") };
            for (int ei = 0; ei < encItems.Length; ei++)
            {
                var (id, lbl) = encItems[ei];
                bool on = string.Equals(id, selectedEnc, StringComparison.OrdinalIgnoreCase);
                var b = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = lbl,
                    Height = 31,
                    MinHeight = 31,
                    CornerRadius = new CornerRadius(15.5),
                    Background = on ? ThemeHelper.Brush(tint.AccentSoft) : new SolidColorBrush(Colors.Transparent),
                    Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T2),
                    FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                    BorderThickness = new Thickness(0),
                    FontSize = 12,
                    UseSystemFocusVisuals = false,
                };
                string? targetEnc = id;
                b.Click += (_, _) => { selectedEnc = targetEnc; RefreshLayout(debounce: false); };
                Grid.SetColumn(b, ei);
                encSegGrid.Children.Add(b);
            }

            // Preview card
            previewCardHost.Children.Clear();
            if (currentLayout is not null && currentLayout.Preview.Length > 0)
            {
                if (!string.IsNullOrWhiteSpace(currentLayout.PreviewTitle))
                {
                    previewCardHost.Children.Add(new TextBlock
                    {
                        Text = currentLayout.PreviewTitle,
                        FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                        FontSize = 13,
                        FontWeight = FontWeights.Bold,
                        Foreground = ThemeHelper.Brush(p.T1),
                    });
                }
                previewCardHost.Children.Add(new TextBlock
                {
                    Text = "　　" + currentLayout.Preview[0],
                    FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                    FontSize = 12.5,
                    Foreground = ThemeHelper.Brush(p.T1),
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 2,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    LineHeight = 21,
                });
            }
            else
            {
                previewCardHost.Children.Add(new TextBlock
                {
                    Text = readFailure ?? (currentLayout?.Error is null ? "正在读…" : "这种编码读不出字"),
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T3),
                });
            }

            if (currentLayout is not null && currentLayout.Unreadable > 0)
            {
                unreadableTb.Text = $"⚠ 有 {currentLayout.Unreadable} 处字认不出来，换一种编码试试";
                unreadableTb.Visibility = Visibility.Visible;
            }
            else
            {
                unreadableTb.Visibility = Visibility.Collapsed;
            }

            // Chapters list (first 3 + 全部 N 章 / long jump)
            chaptersHost.Children.Clear();
            if (!string.IsNullOrEmpty(currentLayout?.Error))
            {
                chaptersHost.Children.Add(new TextBlock
                {
                    Text = currentLayout.Error,
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.Danger),
                    Margin = new Thickness(12, 8, 12, 8),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            else if (currentLayout is not null)
            {
                int? longIdx = currentLayout.Long is uint l ? (int)l : null;
                int shown = Math.Min(3, currentLayout.Chapters.Length);
                for (int i = 0; i < shown; i++)
                {
                    if (i > 0) chaptersHost.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });
                    chaptersHost.Children.Add(MakeChapterRow(currentLayout.Chapters[i], longIdx == i));
                }

                if (currentLayout.Chapters.Length > 3)
                {
                    chaptersHost.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });
                    if (longIdx is int li && li >= 3 && li < currentLayout.Chapters.Length)
                    {
                        var dotsBtn = new Button
                        {
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            Height = 24,
                            Background = new SolidColorBrush(Colors.Transparent),
                            BorderThickness = new Thickness(0),
                            Content = new TextBlock { Text = $"⋯ 全部 {currentLayout.Chapters.Length} 章 ›", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Center },
                        };
                        dotsBtn.Click += (_, _) => { showAllChapters = true; RenderAllChaptersView(); };
                        chaptersHost.Children.Add(dotsBtn);
                        chaptersHost.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });
                        chaptersHost.Children.Add(MakeChapterRow(currentLayout.Chapters[li], isLong: true));
                    }
                    else
                    {
                        var allBtn = new Button
                        {
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            HorizontalContentAlignment = HorizontalAlignment.Stretch,
                            Padding = new Thickness(12, 8, 12, 8),
                            Background = new SolidColorBrush(Colors.Transparent),
                            BorderThickness = new Thickness(0),
                        };
                        var ag = new Grid();
                        ag.Children.Add(new TextBlock { Text = $"全部 {currentLayout.Chapters.Length} 章", FontSize = 12, Foreground = ThemeHelper.Brush(p.T1) });
                        ag.Children.Add(new TextBlock { Text = "›", FontSize = 13, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
                        allBtn.Content = ag;
                        allBtn.Click += (_, _) => { showAllChapters = true; RenderAllChaptersView(); };
                        chaptersHost.Children.Add(allBtn);
                    }
                }
            }

            // Long-chapter anomaly warning card (1:1 TxtLayout.swift:266-310)
            warningHost.Content = null;
            if (currentLayout is not null && currentLayout.Long is uint lu && lu < currentLayout.Chapters.Length)
            {
                int li = (int)lu;
                var c = currentLayout.Chapters[li];
                int n = li + (currentLayout.Chapters.FirstOrDefault()?.Title == "开头" ? 0 : 1);
                bool hasNext = li + 1 < currentLayout.Chapters.Length;
                var sortedSizes = currentLayout.Chapters.Select(x => x.Chars).OrderBy(x => x).ToArray();
                uint usual = sortedSizes[sortedSizes.Length / 2];

                var wStack = new StackPanel { Spacing = 4 };
                wStack.Children.Add(new TextBlock
                {
                    Text = hasNext
                        ? $"⚠ 第 {n} 和 {n + 1} 章之间有 {FormatWan(c.Chars)}字，可能漏了一章"
                        : $"⚠ 第 {n} 章之后有 {FormatWan(c.Chars)}字，可能漏了几章",
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T1),
                    TextWrapping = TextWrapping.Wrap,
                });
                wStack.Children.Add(new TextBlock
                {
                    Text = $"别的章都在 {FormatRoughly(usual)}字上下，多半是这里有一章的标题写法不一样，没认出来。",
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.T2),
                    TextWrapping = TextWrapping.Wrap,
                });

                if (onJumpToSection is not null && !IsChoiceChanged())
                {
                    uint targetSec = c.Section;
                    var goBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                        Content = $"去第 {n} 章看看",
                        Height = 26,
                        MinHeight = 26,
                        FontSize = 11.5,
                        Margin = new Thickness(0, 4, 0, 0),
                    };
                    goBtn.Click += (_, _) =>
                    {
                        pendingJumpSection = targetSec;
                        dlgRef?.Hide();
                    };
                    wStack.Children.Add(goBtn);
                }
                else if (onJumpToSection is not null)
                {
                    wStack.Children.Add(new TextBlock { Text = "点保存后可以去这一章看看", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                }

                warningHost.Content = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(Color.FromArgb(28, p.Warn.R, p.Warn.G, p.Warn.B)),
                    Padding = new Thickness(12, 10, 12, 10),
                    Child = wStack,
                };
            }
            else if (!string.IsNullOrEmpty(currentLayout?.Doubt) && currentLayout.Unreadable == 0)
            {
                warningHost.Content = new TextBlock
                {
                    Text = $"⚠ {currentLayout.Doubt}",
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.Warn),
                    TextWrapping = TextWrapping.Wrap,
                };
            }
        }

        void BuildMainFrame()
        {
            host.Children.Clear();
            var topHead = new Grid();
            var topLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            topLeft.Children.Add(new TextBlock { Text = L10n.T("编码和目录"), FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            topLeft.Children.Add(headerSubtitleTb);
            topHead.Children.Add(topLeft);
            var closeCircle = YYChrome.MakeSheetCloseButton(() => { if (!working) dlgRef?.Hide(); }, dark);
            closeCircle.HorizontalAlignment = HorizontalAlignment.Right;
            topHead.Children.Add(closeCircle);

            var sp = new StackPanel { Spacing = 8 };
            var encHead = new Grid { Margin = new Thickness(2, 2, 2, 0) };
            encHead.Children.Add(new TextBlock { Text = "编码", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2) });
            encHead.Children.Add(encTrailTb);
            sp.Children.Add(encHead);
            sp.Children.Add(encHost);
            sp.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                Padding = new Thickness(14, 11, 14, 11),
                Child = previewCardHost,
            });
            sp.Children.Add(unreadableTb);

            var chapHead = new Grid { Margin = new Thickness(2, 6, 2, 0) };
            chapHead.Children.Add(new TextBlock { Text = "目录", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2) });
            chapHead.Children.Add(chapTrailTb);
            sp.Children.Add(chapHead);

            var chapCard = new StackPanel { Spacing = 0 };
            var ruleRow = new Grid { Padding = new Thickness(14, 6, 12, 6), ColumnSpacing = 10, MinHeight = 38 };
            ruleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            ruleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ruleRow.Children.Add(new TextBlock { Text = "规则", FontSize = 12, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(ruleBox, 1);
            ruleRow.Children.Add(ruleBox);
            chapCard.Children.Add(ruleRow);
            chapCard.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) });
            chapCard.Children.Add(chaptersHost);

            sp.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                Child = chapCard,
            });

            resetRuleBtn.Visibility = !string.IsNullOrWhiteSpace(ruleText) ? Visibility.Visible : Visibility.Collapsed;
            var repaginateBtn = new Button
            {
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2),
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Foreground = ThemeHelper.Brush(tint.Accent),
                HorizontalAlignment = HorizontalAlignment.Right,
                UseSystemFocusVisuals = false,
                Content = L10n.T("重新分章"),
            };
            var ruleResetRow = new Grid();
            ruleResetRow.Children.Add(resetRuleBtn);
            ruleResetRow.Children.Add(repaginateBtn);
            sp.Children.Add(ruleResetRow);
            sp.Children.Add(warningHost);

            var workingRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(2, 4, 2, 0),
                Children =
                {
                    new ProgressRing { IsActive = true, Width = 16, Height = 16 },
                    new TextBlock
                    {
                        Text = L10n.T("正在按新的方式整理这本书…"),
                        FontSize = 12,
                        Foreground = ThemeHelper.Brush(p.T2),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };
            var applyErrorTb = new TextBlock
            {
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.Danger),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(2, 2, 2, 0),
            };
            sp.Children.Add(workingRow);
            sp.Children.Add(applyErrorTb);

            var saveSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            saveSp.Children.Add(new TextBlock { Text = L10n.T("保存"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
            saveSp.Children.Add(new TextBlock { Text = "↵", FontSize = 11.5, Foreground = ThemeHelper.Brush(tint.Accent), Opacity = 0.72, VerticalAlignment = VerticalAlignment.Center });

            Button? cancelSheetBtn = null;
            void SetSheetWorking(bool busy)
            {
                working = busy;
                workingRow.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                closeCircle.IsEnabled = !busy;
                if (cancelSheetBtn is not null) cancelSheetBtn.IsEnabled = !busy;
                if (saveSheetBtn is not null) saveSheetBtn.IsEnabled = !busy && string.IsNullOrEmpty(currentLayout?.Error);
                ruleBox.IsEnabled = !busy;
                encHost.IsHitTestVisible = !busy;
                resetRuleBtn.IsEnabled = !busy;
                repaginateBtn.IsEnabled = !busy;
            }

            repaginateBtn.Click += async (_, _) =>
            {
                if (working) return;
                selectedEnc = null;
                ruleText = "";
                ruleBox.Text = "";
                resetRuleBtn.Visibility = Visibility.Collapsed;
                applyErrorTb.Visibility = Visibility.Collapsed;
                SetSheetWorking(true);
                try
                {
                    beforeRebuild?.Invoke();
                    var autoChoice = new TxtChoice(null, null);
                    await Task.Run(() => library.Core.SetTxtChoice(bookId, autoChoice));
                    applied = autoChoice;
                    confirmedSave = true;
                    SetSheetWorking(false);
                    dlgRef?.Hide();
                }
                catch (CoreException.Invalid inv)
                {
                    SetSheetWorking(false);
                    applyErrorTb.Text = inv.detail;
                    applyErrorTb.Visibility = Visibility.Visible;
                }
                catch (Exception ex)
                {
                    SetSheetWorking(false);
                    applyErrorTb.Text = ex.Message;
                    applyErrorTb.Visibility = Visibility.Visible;
                }
            };

            cancelSheetBtn = YYChrome.MakeSheetPillButton("取消", () => { if (!working) dlgRef?.Hide(); }, dark, library.Prefs.Tint, "secondary", height: 36, horizontalPadding: 18);
            saveSheetBtn = YYChrome.MakeSheetPillButton(saveSp, async () =>
            {
                if (working) return;
                if (!IsChoiceChanged())
                {
                    dlgRef?.Hide();
                    return;
                }

                applyErrorTb.Visibility = Visibility.Collapsed;
                SetSheetWorking(true);
                try
                {
                    beforeRebuild?.Invoke();
                    var finalChoice = new TxtChoice(selectedEnc, string.IsNullOrWhiteSpace(ruleText) ? null : ruleText.Trim());
                    await Task.Run(() => library.Core.SetTxtChoice(bookId, finalChoice));
                    confirmedSave = true;
                    SetSheetWorking(false);
                    dlgRef?.Hide();
                }
                catch (CoreException.Invalid inv)
                {
                    SetSheetWorking(false);
                    applyErrorTb.Text = inv.detail;
                    applyErrorTb.Visibility = Visibility.Visible;
                }
                catch (Exception ex)
                {
                    SetSheetWorking(false);
                    applyErrorTb.Text = ex.Message;
                    applyErrorTb.Visibility = Visibility.Visible;
                }
            }, dark, library.Prefs.Tint, "warm", height: 36, horizontalPadding: 18);

            var footActs = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children =
                {
                    cancelSheetBtn,
                    saveSheetBtn,
                },
            };
            var footRow = new Grid { Margin = new Thickness(2, 4, 0, 0) };
            footRow.Children.Add(new TextBlock { Text = "自动识别还认得序章、楔子、Chapter 1", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
            footRow.Children.Add(footActs);

            var outer = new StackPanel
            {
                Spacing = 12,
                Padding = new Thickness(22, 18, 22, 18),
                Children =
                {
                    topHead,
                    new ScrollViewer { MaxHeight = 430, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = sp },
                    footRow,
                },
            };
            host.Children.Add(outer);
        }

        BuildMainFrame();
        RenderMainContent();
        RefreshLayout(debounce: false);

        dlgRef = YYChrome.CreateMacSheet(xamlRoot, dark, 504, host, maxHeight: 780, tint: library.Prefs.Tint);
        dlgRef.Closing += (_, args) =>
        {
            if (working) args.Cancel = true;
        };
        await dlgRef.ShowSafeAsync(library.Prefs.Tint);
        debounceCts?.Cancel();

        if (pendingJumpSection is uint jumpSec)
        {
            onJumpToSection?.Invoke(jumpSec);
            return false;
        }

        return confirmedSave;
    }
}

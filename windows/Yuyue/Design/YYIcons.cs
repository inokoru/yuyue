using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Yuyue.Design;

/// <summary>
/// Crisp 1.5px-stroke vector icons ported 1:1 from <c>apple/Yuyue/Assets.xcassets</c>,
/// <c>MacShelfView.swift (ProtoShape)</c>, and <c>MacSettings.swift (SF Symbols)</c>.
/// </summary>
public static class YYIcons
{
    public static FrameworkElement Make(string name, double size = 14, Brush? stroke = null, bool filled = false)
    {
        var brush = stroke ?? (Brush)Application.Current.Resources["YYText2Brush"];
        var (data, viewBox, isFill) = name switch
        {
            // Shelf sidebar icons (01-shelf.png)
            "shelf" => ("M2.75,5.45 A1.2,1.2 0 0,1 3.95,4.25 H6.05 A1.2,1.2 0 0,1 7.25,5.45 V18.55 A1.2,1.2 0 0,1 6.05,19.75 H3.95 A1.2,1.2 0 0,1 2.75,18.55 Z M8.75,7.95 A1.2,1.2 0 0,1 9.95,6.75 H12.05 A1.2,1.2 0 0,1 13.25,7.95 V18.55 A1.2,1.2 0 0,1 12.05,19.75 H9.95 A1.2,1.2 0 0,1 8.75,18.55 Z M17.67,19.75 L21.73,18.66 L18.31,5.91 L14.25,7 Z", 24.0, true),
            "book" => ("M6.5,3.5 H17 A1,1 0 0,1 18,4.5 V19.5 A1,1 0 0,1 17,20.5 H6.5 A1.5,1.5 0 0,1 5,19 V5 A1.5,1.5 0 0,1 6.5,3.5 Z M5,17.5 H18 M14,3.5 V10.5 L12.25,9.1 L10.5,10.5 V3.5", 24.0, false),
            "radio" => ("M6,9 H18 A3,3 0 0,1 21,12 V17.25 A3,3 0 0,1 18,20.25 H6 A3,3 0 0,1 3,17.25 V12 A3,3 0 0,1 6,9 Z M7.5,9 L17.25,3.75 M6.75,14.55 H9.75 M15.75,12.15 A2.4,2.4 0 1,1 15.74,12.15 Z", 24.0, false),
            "wave" => ("M2,8 H2.5 M4.5,5.5 V10.5 M7,3 V13 M9.5,5 V11 M12,6.5 V9.5 M14,8 H14.5", 16.0, false),
            // SF Symbol highlighter / pencil.line (笔记 in 01-shelf.png)
            "highlighter" => ("M9.8,2.6 L13.4,6.2 L6.5,13.1 H2.9 V9.5 Z M8.1,4.3 L11.7,7.9 M2.5,14.2 H13.5", 16.0, false),
            // SF Symbol translate (overlapping A / 文 tiles in 01-shelf.png & 04-english.png)
            "a-wen" => ("M2.2,2.2 H8.8 A1.4,1.4 0 0,1 10.2,3.6 V8.8 A1.4,1.4 0 0,1 8.8,10.2 H2.2 A1.4,1.4 0 0,1 0.8,8.8 V3.6 A1.4,1.4 0 0,1 2.2,2.2 Z M4.3,8.2 L5.5,4.3 L6.7,8.2 M4.6,7.1 H6.4 M10.2,5.8 H13.8 A1.4,1.4 0 0,1 15.2,7.2 V12.6 A1.4,1.4 0 0,1 13.8,14 H7.2 A1.4,1.4 0 0,1 5.8,12.6 V10.2 M9.2,8.5 H12.8 M11,7.5 V8.5 M9.8,8.5 C10.1,10.2 11.2,11.4 12.6,12.2 M12.2,8.5 C11.8,10.3 10.6,11.5 9.1,12.3", 16.0, false),
            "translate" => ("M2.5,4 H8.5 M5.5,2.5 V4 M4,4 C4.3,6.2 5.8,8 8,9 M7.5,4 C7.1,6.4 5.5,8.3 3,9.3 M9,13.5 L11.5,7.5 L14,13.5 M9.8,11.8 H13.2", 16.0, false),
            "stack" => ("M4.5,5.5 H11.5 A1.5,1.5 0 0,1 13,7 V12 A1.5,1.5 0 0,1 11.5,13.5 H4.5 A1.5,1.5 0 0,1 3,12 V7 A1.5,1.5 0 0,1 4.5,5.5 Z M4.5,3.5 H11.5 M6,1.8 H10", 16.0, false),

            // Settings 11 sidebar SF Symbols (05-sync.png & MacSettings.swift)
            // 1. slider.horizontal.3 (通用)
            "sliders" => ("M2.2,4.2 H13.8 M2.2,8 H13.8 M2.2,11.8 H13.8 M5.5,2.8 V5.6 M10.5,6.6 V9.4 M6.8,10.4 V13.2", 16.0, false),
            // 2. sun.max (外观)
            "sun" => ("M8,5.2 A2.8,2.8 0 1,1 7.99,5.2 Z M8,1.5 V2.9 M8,13.1 V14.5 M1.5,8 H2.9 M13.1,8 H14.5 M3.4,3.4 L4.4,4.4 M11.6,11.6 L12.6,12.6 M12.6,3.4 L11.6,4.4 M4.4,11.6 L3.4,12.6", 16.0, false),
            // 3. book (open book — 阅读)
            "book-open" => ("M8,4.2 C6.6,3.1 4.4,2.9 2.2,3.4 V12.6 C4.4,12.1 6.6,12.3 8,13.4 C9.4,12.3 11.6,12.1 13.8,12.6 V3.4 C11.6,2.9 9.4,3.1 8,4.2 Z M8,4.2 V13.4", 16.0, false),
            // 4. character.book.closed (外文)
            "char-book" => ("M4.2,2.2 H12 A1.2,1.2 0 0,1 13.2,3.4 V12.6 A1.2,1.2 0 0,1 12,13.8 H4.2 A1.4,1.4 0 0,1 2.8,12.4 V3.6 A1.4,1.4 0 0,1 4.2,2.2 Z M2.8,11.6 H13.2 M6.2,5.6 H10.2 M8.2,4.5 V5.6 M6.8,7.4 H9.6 L8.2,9.2 V10.2 M6.5,9.2 H9.9", 16.0, false),
            // 5. headphones (朗读)
            "headphones" => ("M3,10 V8 A5,5 0 0,1 13,8 V10 M2.8,9.5 H5 A0.8,0.8 0 0,1 5.8,10.3 V12.7 A0.8,0.8 0 0,1 5,13.5 H2.8 A0.8,0.8 0 0,1 2,12.7 V10.3 A0.8,0.8 0 0,1 2.8,9.5 Z M11,9.5 H13.2 A0.8,0.8 0 0,1 14,10.3 V12.7 A0.8,0.8 0 0,1 13.2,13.5 H11 A0.8,0.8 0 0,1 10.2,12.7 V10.3 A0.8,0.8 0 0,1 11,9.5 Z", 16.0, false),
            // 6. text.bubble (书伴)
            "bubble-lines" => ("M3.5,2.8 H12.5 A1.6,1.6 0 0,1 14.1,4.4 V10.2 A1.6,1.6 0 0,1 12.5,11.8 H7 L4,14.1 V11.8 H3.5 A1.6,1.6 0 0,1 1.9,10.2 V4.4 A1.6,1.6 0 0,1 3.5,2.8 Z M5.2,6.1 H10.8 M5.2,8.6 H9.2", 16.0, false),
            // 7. arrow.triangle.2.circlepath (同步)
            "sync-arrows" => ("M12.8,6.2 A5.1,5.1 0 0,0 4.1,4.7 M3.6,2.6 V5.1 H6.1 M3.2,9.8 A5.1,5.1 0 0,0 11.9,11.3 M12.4,13.4 V10.9 H9.9", 16.0, false),
            // 8. square.and.arrow.up (笔记导出 / 导出)
            "share-up" => ("M8,2.2 V9.8 M5.4,4.8 L8,2.2 L10.6,4.8 M4.8,6.8 H3.8 A1.4,1.4 0 0,0 2.4,8.2 V12.4 A1.4,1.4 0 0,0 3.8,13.8 H12.2 A1.4,1.4 0 0,0 13.6,12.4 V8.2 A1.4,1.4 0 0,0 12.2,6.8 H11.2", 16.0, false),
            // 9. internaldrive (存储)
            "drive" => ("M3.6,3.4 L12.4,3.4 L14,8.8 V12.2 A1.2,1.2 0 0,1 12.8,13.4 H3.2 A1.2,1.2 0 0,1 2,12.2 V8.8 Z M2.2,9 H13.8 M5,11.2 H8.5 M11.4,11.2 H11.6", 16.0, false),
            // 10. shippingbox (数据)
            "cube" => ("M8,1.8 L13.6,4.9 V11.1 L8,14.2 L2.4,11.1 V4.9 Z M2.4,4.9 L8,8 L13.6,4.9 M8,8 V14.2", 16.0, false),
            // 11. info.circle (关于)
            "info-circle" => ("M8,2 A6,6 0 1,1 7.99,2 Z M8,7.3 V11.2 M8,4.9 H8.05", 16.0, false),

            // Sync & Reader extra icons
            "cloud" => ("M4.8,12.4 H11.6 A2.7,2.7 0 0,0 12.1,7.1 A3.9,3.9 0 0,0 4.6,6.1 A2.9,2.9 0 0,0 4.8,12.4 Z", 16.0, false),
            "cloud-slash" => ("M4.8,12.4 H11.4 M12.2,7.2 A3.8,3.8 0 0,0 5.3,5.2 M2.4,2.4 L13.6,13.6 M3.1,7.5 A2.8,2.8 0 0,0 4.8,12.4", 16.0, false),
            "server" => ("M3.5,2.8 H12.5 A1.2,1.2 0 0,1 13.7,4 V6.4 A1.2,1.2 0 0,1 12.5,7.6 H3.5 A1.2,1.2 0 0,1 2.3,6.4 V4 A1.2,1.2 0 0,1 3.5,2.8 Z M8,7.6 V11.8 M4.2,11.8 H11.8 M6.8,10.8 H9.2 V12.8 H6.8 Z", 16.0, false),
            "globe" => ("M8,2 A6,6 0 1,1 7.99,2 Z M2.2,8 H13.8 M8,2 C9.8,3.8 9.8,12.2 8,14 M8,2 C6.2,3.8 6.2,12.2 8,14", 16.0, false),
            "laptop" => ("M3.4,3.6 H12.6 A1,1 0 0,1 13.6,4.6 V10.6 H2.4 V4.6 A1,1 0 0,1 3.4,3.6 Z M1.4,12.2 H14.6", 16.0, false),
            "lock" => ("M4.4,7.4 H11.6 A1,1 0 0,1 12.6,8.4 V12.8 A1,1 0 0,1 11.6,13.8 H4.4 A1,1 0 0,1 3.4,12.8 V8.4 A1,1 0 0,1 4.4,7.4 Z M5.5,7.4 V5.2 A2.5,2.5 0 0,1 10.5,5.2 V7.4", 16.0, false),
            "clock-history" => ("M3.2,5.2 A5.5,5.5 0 1,1 2.7,8.8 M2.6,2.8 V5.5 H5.3 M8,5.2 V8.2 L10.2,9.5", 16.0, false),
            "checkmark" => ("M3.2,8.4 L6.4,11.6 L12.8,4.6", 16.0, false),
            "folder" => ("M2.2,4.4 A1.2,1.2 0 0,1 3.4,3.2 H6.3 L7.6,4.8 H12.6 A1.2,1.2 0 0,1 13.8,6 V11.8 A1.2,1.2 0 0,1 12.6,13 H3.4 A1.2,1.2 0 0,1 2.2,11.8 Z", 16.0, false),
            "speaker" => ("M2.8,6.2 H5.2 L8.4,3.5 V12.5 L5.2,9.8 H2.8 Z M10.6,5.6 C11.5,6.8 11.5,9.2 10.6,10.4 M12.4,4 C14,6.2 14,9.8 12.4,12", 16.0, false),
            "cast" => ("M5.6,3.8 A2.1,2.1 0 1,1 5.59,3.8 Z M2.4,12.6 C2.6,10.3 4,9.2 5.6,9.2 C7.2,9.2 8.6,10.3 8.8,12.6 M10.8,5.2 C11.8,6.5 11.8,8.7 10.8,10 M12.7,3.8 C14.2,5.8 14.2,9.4 12.7,11.4", 16.0, false),
            "arrow-up-right" => ("M4.5,11.5 L11.5,4.5 M6,4.5 H11.5 V10", 16.0, false),

            // Shared UI icons
            "note-card" => ("M6.5,4 H17.5 A2.5,2.5 0 0,1 20,6.5 V14 L14,20 H6.5 A2.5,2.5 0 0,1 4,17.5 V6.5 A2.5,2.5 0 0,1 6.5,4 Z M14,20 V15.5 A1.5,1.5 0 0,1 15.5,14 H20 M8,9 H16 M8,12.5 H13", 24.0, false),
            "search" => ("M7,2.8 A4.2,4.2 0 1,1 6.99,2.8 Z M10.2,10.2 L13.5,13.5", 16.0, false),
            "sidebar" => ("M3.8,3 H12.2 A1.8,1.8 0 0,1 14,4.8 V11.2 A1.8,1.8 0 0,1 12.2,13 H3.8 A1.8,1.8 0 0,1 2,11.2 V4.8 A1.8,1.8 0 0,1 3.8,3 Z M6,3 V13", 16.0, false),
            "plus" => ("M8,3 V13 M3,8 H13", 16.0, false),
            "updown" => ("M5,6 L8,3 L11,6 M5,10 L8,13 L11,10", 16.0, false),
            "sparkle" => ("M8,1.8 C8.4,5.4 10.6,7.6 14.2,8 C10.6,8.4 8.4,10.6 8,14.2 C7.6,10.6 5.4,8.4 1.8,8 C5.4,7.6 7.6,5.4 8,1.8 Z", 16.0, false),
            "feedback" => ("M3.5,3 H12.5 A1.5,1.5 0 0,1 14,4.5 V10.5 A1.5,1.5 0 0,1 12.5,12 H7 L4,14.2 V12 H3.5 A1.5,1.5 0 0,1 2,10.5 V4.5 A1.5,1.5 0 0,1 3.5,3 Z M8,5.5 V8.2 M8,10 H8.05", 16.0, false),
            "gear" => ("M8,5.6 A2.4,2.4 0 1,1 7.99,5.6 Z M8,1.8 V3.3 M8,12.7 V14.2 M1.8,8 H3.3 M12.7,8 H14.2 M3.6,3.6 L4.7,4.7 M11.3,11.3 L12.4,12.4 M12.4,3.6 L11.3,4.7 M4.7,11.3 L3.6,12.4", 16.0, false),
            "list" => ("M5.5,4.2 H13.5 M5.5,8 H13.5 M5.5,11.8 H13.5 M2.5,4.2 H3 M2.5,8 H3 M2.5,11.8 H3", 16.0, false),
            "bookmark" => ("M4.5,2.5 H11.5 V13.5 L8,11 L4.5,13.5 Z", 16.0, filled),
            "play" => ("M4.5,3 L13,8 L4.5,13 Z", 16.0, true),
            "pause" => ("M4.5,3 H7 V13 H4.5 Z M9,3 H11.5 V13 H9 Z", 16.0, true),
            "chevron-left" => ("M10,3.2 L5.2,8 L10,12.8", 16.0, false),
            "chevron-right" => ("M6,3.2 L10.8,8 L6,12.8", 16.0, false),
            "chevron-down" => ("M3.5,6 L8,10.5 L12.5,6", 16.0, false),
            "chevron-up" => ("M3.5,10 L8,5.5 L12.5,10", 16.0, false),
            "col-1" => ("M3.5,2.5 H12.5 A1,1 0 0,1 13.5,3.5 V12.5 A1,1 0 0,1 12.5,13.5 H3.5 A1,1 0 0,1 2.5,12.5 V3.5 A1,1 0 0,1 3.5,2.5 Z M5.2,6 H10.8 M5.2,8.5 H10.8 M5.2,11 H8.8", 16.0, false),
            "col-2" => ("M2.5,3.2 H13.5 A1,1 0 0,1 14.5,4.2 V11.8 A1,1 0 0,1 13.5,12.8 H2.5 A1,1 0 0,1 1.5,11.8 V4.2 A1,1 0 0,1 2.5,3.2 Z M8,3.2 V12.8", 16.0, false),
            "col-scroll" => ("M3.5,2 H12.5 A1,1 0 0,1 13.5,3 V13 A1,1 0 0,1 12.5,14 H3.5 A1,1 0 0,1 2.5,13 V3 A1,1 0 0,1 3.5,2 Z M2.5,8 H13.5", 16.0, false),
            "doc-search" => ("M4.2,2.2 H9.8 L12.8,5.2 V8.6 M4.2,2.2 A1.2,1.2 0 0,0 3,3.4 V12.6 A1.2,1.2 0 0,0 4.2,13.8 H8.4 M5.2,6 H8.8 M5.2,8.4 H7.6 M10.6,9.4 A2.1,2.1 0 1,1 10.59,9.4 Z M12.2,12.4 L14,14.2", 16.0, false),
            "goback-15" => ("M8,3.2 A5.2,5.2 0 1,1 3.2,9.8 M8,1.5 L5.6,3.4 L8,5.3 M6.2,7.3 V10.8 M8.5,7.3 H10.6 M8.5,7.3 V8.9 H10.3 A0.9,0.9 0 0,1 10.3,10.8 H8.4", 16.0, false),
            "goforward-30" => ("M8,3.2 A5.2,5.2 0 1,0 12.8,9.8 M8,1.5 L10.4,3.4 L8,5.3 M5.4,7.4 H7.3 L6.2,8.9 H7.1 A0.9,0.9 0 0,1 7.1,10.8 H5.4 M9.8,7.3 A1.1,1.75 0 1,1 9.79,7.3 Z", 16.0, false),
            "moon" => ("M11.6,10.4 A4.8,4.8 0 0,1 5.6,4.4 A4.9,4.9 0 1,0 11.6,10.4 Z", 16.0, filled),
            "pin" => ("M6,2.5 H10 L9.5,6.8 L11.5,8.8 V9.8 H4.5 V8.8 L6.5,6.8 Z M8,9.8 V13.8", 16.0, filled),
            "pencil" => ("M10.8,2.6 L13.4,5.2 L5.8,12.8 H3.2 V10.2 Z M9.2,4.2 L11.8,6.8", 16.0, false),
            "trash" => ("M3.2,4.5 H12.8 M6.2,4.5 V3 A0.8,0.8 0 0,1 7,2.2 H9 A0.8,0.8 0 0,1 9.8,3 V4.5 M4.4,4.5 L5,13 A1,1 0 0,0 6,13.8 H10 A1,1 0 0,0 11,13 L11.6,4.5 M6.8,7 V11.2 M9.2,7 V11.2", 16.0, false),
            "copy" => ("M5.8,5.2 H12.2 A1.2,1.2 0 0,1 13.4,6.4 V12.6 A1.2,1.2 0 0,1 12.2,13.8 H5.8 A1.2,1.2 0 0,1 4.6,12.6 V6.4 A1.2,1.2 0 0,1 5.8,5.2 Z M3.8,10.8 H3.2 A1.2,1.2 0 0,1 2,9.6 V3.4 A1.2,1.2 0 0,1 3.2,2.2 H9.6 A1.2,1.2 0 0,1 10.8,3.4 V4", 16.0, false),
            "check-circle" => ("M8,2 A6,6 0 1,1 7.99,2 Z M5.2,8.2 L7.1,10.1 L10.9,6.1", 16.0, false),
            "pause-circle" => ("M8,2 A6,6 0 1,1 7.99,2 Z M6.5,5.8 V10.2 M9.5,5.8 V10.2", 16.0, false),
            "xmark-circle" => ("M8,2 A6,6 0 1,1 7.99,2 Z M5.8,5.8 L10.2,10.2 M10.2,5.8 L5.8,10.2", 16.0, false),
            "xmark" => ("M4,4 L12,12 M12,4 L4,12", 16.0, false),
            "arrow-undo" => ("M5.5,4.2 L2.8,6.8 L5.5,9.4 M3,6.8 H9.8 A3.2,3.2 0 0,1 9.8,13.2 H6.5", 16.0, false),
            "arrow-down" => ("M8,2.5 V11.2 M4.8,8.2 L8,11.4 L11.2,8.2 M3.2,13.5 H12.8", 16.0, false),
            "refresh" => ("M13.2,5.2 A5.2,5.2 0 1,0 13.4,9.8 M13.4,2.4 V5.4 H10.4", 16.0, false),
            "people" => ("M6,4.2 A2,2 0 1,1 5.99,4.2 Z M2.4,12.6 C2.6,10.4 4.1,9.3 6,9.3 C7.9,9.3 9.4,10.4 9.6,12.6 M10.6,4.8 A1.7,1.7 0 1,1 10.59,4.8 Z M10.6,9.5 C12.1,9.6 13.3,10.6 13.6,12.6", 16.0, false),
            "repeat-1" => ("M3.2,5.5 H12.2 L10.4,3.7 M12.8,10.5 H3.8 L5.6,12.3 M8.2,6.6 V9.6", 16.0, false),
            "eye-slash" => ("M2.2,8 C3.8,5.1 5.8,4 8,4 C10.2,4 12.2,5.1 13.8,8 C12.2,10.9 10.2,12 8,12 C5.8,12 3.8,10.9 2.2,8 Z M2.8,2.8 L13.2,13.2", 16.0, false),
            "mic" => ("M8,2.2 A2,2 0 0,1 10,4.2 V7.8 A2,2 0 0,1 6,7.8 V4.2 A2,2 0 0,1 8,2.2 Z M4.2,7.6 A3.8,3.8 0 0,0 11.8,7.6 M8,11.4 V13.8 M5.8,13.8 H10.2", 16.0, false),
            "grid-all" => ("M2.8,2.8 H6.8 V6.8 H2.8 Z M9.2,2.8 H13.2 V6.8 H9.2 Z M2.8,9.2 H6.8 V13.2 H2.8 Z M9.2,9.2 H13.2 V13.2 H9.2 Z", 16.0, false),
            "heart" => ("M8,13.2 C8,13.2 2.4,9.6 2.4,5.8 A2.9,2.9 0 0,1 8,4.6 A2.9,2.9 0 0,1 13.6,5.8 C13.6,9.6 8,13.2 8,13.2 Z", 16.0, filled),
            "clock" => ("M8,2 A6,6 0 1,1 7.99,2 Z M8,4.8 V8.2 L10.4,9.6", 16.0, false),
            "lh-tight" => ("M2.8,5 H13.2 M2.8,8 H13.2 M2.8,11 H13.2", 16.0, false),
            "lh-normal" => ("M2.8,3.8 H13.2 M2.8,8 H13.2 M2.8,12.2 H13.2", 16.0, false),
            "lh-loose" => ("M2.8,2.6 H13.2 M2.8,8 H13.2 M2.8,13.4 H13.2", 16.0, false),
            "more" => ("M3.6,8 A1.15,1.15 0 1,1 3.59,8 Z M8,8 A1.15,1.15 0 1,1 7.99,8 Z M12.4,8 A1.15,1.15 0 1,1 12.39,8 Z", 16.0, true),
            _ => ("M4,4 H12 V12 H4 Z", 16.0, false),
        };

        var geometry = ParseGeometry(data);
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Stretch = Stretch.Uniform,
            StrokeThickness = viewBox > 20 ? 1.65 : (name == "a-wen" ? 1.25 : 1.45),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };

        if (isFill || filled)
        {
            path.Fill = brush;
            if (name == "shelf")
            {
                path.Stroke = brush;
                path.StrokeThickness = 1.2;
            }
        }
        else
        {
            path.Stroke = brush;
        }

        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = new Grid
            {
                Width = viewBox,
                Height = viewBox,
                Children = { path },
            },
        };
    }

    public static Geometry ParseGeometry(string data) =>
        (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data);

    /// <summary>
    /// Ported 1:1 from <c>Shelf.swift (CollectionIcon.symbol)</c>: infers a semantic glyph from collection name.
    /// </summary>
    public static string CollectionIconName(string name, string? customIcon = null)
    {
        if (!string.IsNullOrWhiteSpace(customIcon)) return customIcon;
        if (name.Contains('睡') || name.Contains('夜') || name.Contains("晚安")) return "moon";
        if (name.Contains("科幻") || name.Contains('星')) return "sparkle";
        if (name.Contains('听') || name.Contains("通勤") || name.Contains("有声")) return "headphones";
        if (name.Contains("英文") || name.Contains("外文") || name.Contains("English", StringComparison.OrdinalIgnoreCase)) return "globe";
        if (name.Contains("最爱") || name.Contains("喜欢")) return "heart";
        if (name.Contains("历史")) return "clock";
        if (name.Contains("重读")) return "sync-arrows";
        if (name.Contains('写') || name.Contains("随笔")) return "pencil";
        if (name.Contains("文学") || name.Contains("经典")) return "book";
        return "stack";
    }
}

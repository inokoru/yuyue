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
            // Shelf sidebar icons (shelf/1.png & MacShelfView.swift:357-388)
            "shelf" => ("M2.75,5.45 A1.2,1.2 0 0,1 3.95,4.25 H6.05 A1.2,1.2 0 0,1 7.25,5.45 V18.55 A1.2,1.2 0 0,1 6.05,19.75 H3.95 A1.2,1.2 0 0,1 2.75,18.55 Z M8.75,7.95 A1.2,1.2 0 0,1 9.95,6.75 H12.05 A1.2,1.2 0 0,1 13.25,7.95 V18.55 A1.2,1.2 0 0,1 12.05,19.75 H9.95 A1.2,1.2 0 0,1 8.75,18.55 Z M17.67,19.75 L21.73,18.66 L18.31,5.91 L14.25,7 Z", 24.0, true),
            "book" => ("M6.5,3.5 H17 A1,1 0 0,1 18,4.5 V19.5 A1,1 0 0,1 17,20.5 H6.5 A1.5,1.5 0 0,1 5,19 V5 A1.5,1.5 0 0,1 6.5,3.5 Z M5,17.5 H18 M14,3.5 V10.5 L12.25,9.1 L10.5,10.5 V3.5", 24.0, false),
            "radio" => ("M6,9 H18 A3,3 0 0,1 21,12 V17.25 A3,3 0 0,1 18,20.25 H6 A3,3 0 0,1 3,17.25 V12 A3,3 0 0,1 6,9 Z M7.5,9 L17.25,3.75 M6.75,14.55 H9.75 M15.75,12.15 A2.4,2.4 0 1,1 15.74,12.15 Z", 24.0, false),
            "wave" or "waveform" => ("M2,8 H2.5 M4.5,5.5 V10.5 M7,3 V13 M9.5,5 V11 M12,6.5 V9.5 M14,8 H14.5", 16.0, false),
            // SF Symbol square.and.pencil (笔记 in shelf/1.png & MacShelfView.swift:371)
            "square.and.pencil" or "notes-edit" => ("M8.8,3.2 H4.2 A1.6,1.6 0 0,0 2.6,4.8 V12 A1.6,1.6 0 0,0 4.2,13.6 H11.4 A1.6,1.6 0 0,0 13,12 V7.4 M11.8,2.1 L13.9,4.2 L8.3,9.8 H6.2 V7.7 Z", 16.0, false),
            "highlighter" => ("M8.8,3.2 H4.2 A1.6,1.6 0 0,0 2.6,4.8 V12 A1.6,1.6 0 0,0 4.2,13.6 H11.4 A1.6,1.6 0 0,0 13,12 V7.4 M11.8,2.1 L13.9,4.2 L8.3,9.8 H6.2 V7.7 Z", 16.0, false),
            // SF Symbol translate (overlapping A / 文 tiles in shelf/1.png & MacReaderView.swift:500)
            "a-wen" => ("M2.2,2.2 H8.8 A1.4,1.4 0 0,1 10.2,3.6 V8.8 A1.4,1.4 0 0,1 8.8,10.2 H2.2 A1.4,1.4 0 0,1 0.8,8.8 V3.6 A1.4,1.4 0 0,1 2.2,2.2 Z M4.3,8.2 L5.5,4.3 L6.7,8.2 M4.6,7.1 H6.4 M10.2,5.8 H13.8 A1.4,1.4 0 0,1 15.2,7.2 V12.6 A1.4,1.4 0 0,1 13.8,14 H7.2 A1.4,1.4 0 0,1 5.8,12.6 V10.2 M9.2,8.5 H12.8 M11,7.5 V8.5 M9.8,8.5 C10.1,10.2 11.2,11.4 12.6,12.2 M12.2,8.5 C11.8,10.3 10.6,11.5 9.1,12.3", 16.0, false),
            "translate" => ("M2.5,4 H8.5 M5.5,2.4 V4 M4,4 C4.3,6.2 5.8,8 8,9 M7.5,4 C7.1,6.4 5.5,8.3 3,9.3 M9.2,13.4 L11.6,7.6 L14,13.4 M10,11.7 H13.2", 16.0, false),
            "stack" or "square.stack" => ("M4.3,5.8 H11.7 A1.5,1.5 0 0,1 13.2,7.3 V12.3 A1.5,1.5 0 0,1 11.7,13.8 H4.3 A1.5,1.5 0 0,1 2.8,12.3 V7.3 A1.5,1.5 0 0,1 4.3,5.8 Z M4,4 H12 M5.4,2.3 H10.6", 16.0, false),

            // Settings 11 sidebar SF Symbols (settings-general/2.png & MacSettings.swift)
            // 1. slider.horizontal.3 (通用 — 3 horizontal tracks with hollow circular knobs, NOT vertical crosses!)
            "sliders" => ("M2,4.2 H4.4 M7.4,4.2 H14 M5.9,2.7 A1.5,1.5 0 1,1 5.89,2.7 Z M2,8 H9.1 M12.1,8 H14 M10.6,6.5 A1.5,1.5 0 1,1 10.59,6.5 Z M2,11.8 H5.3 M8.3,11.8 H14 M6.8,10.3 A1.5,1.5 0 1,1 6.79,10.3 Z", 16.0, false),
            // 2. sun.max (外观)
            "sun" => ("M8,5.2 A2.8,2.8 0 1,1 7.99,5.2 Z M8,1.5 V2.9 M8,13.1 V14.5 M1.5,8 H2.9 M13.1,8 H14.5 M3.4,3.4 L4.4,4.4 M11.6,11.6 L12.6,12.6 M12.6,3.4 L11.6,4.4 M4.4,11.6 L3.4,12.6", 16.0, false),
            // 3. book (open book — 阅读)
            "book-open" => ("M8,4.2 C6.6,3.1 4.4,2.9 2.2,3.4 V12.6 C4.4,12.1 6.6,12.3 8,13.4 C9.4,12.3 11.6,12.1 13.8,12.6 V3.4 C11.6,2.9 9.4,3.1 8,4.2 Z M8,4.2 V13.4", 16.0, false),
            // 4. character.book.closed (外文 — closed book + inner framed square)
            "char-book" => ("M4.3,2 H12 A1.2,1.2 0 0,1 13.2,3.2 V12.8 A1.2,1.2 0 0,1 12,14 H4.3 A1.4,1.4 0 0,1 2.9,12.6 V3.4 A1.4,1.4 0 0,1 4.3,2 Z M2.9,11.8 H13.2 M5.3,4.2 H10.8 V9.6 H5.3 Z M6.6,5.8 H9.5 M8.05,4.9 V5.8 M6.8,7.3 H9.3 M8.05,7.3 V8.8", 16.0, false),
            // 5. headphones (朗读)
            "headphones" => ("M3,10 V8 A5,5 0 0,1 13,8 V10 M2.8,9.5 H5 A0.8,0.8 0 0,1 5.8,10.3 V12.7 A0.8,0.8 0 0,1 5,13.5 H2.8 A0.8,0.8 0 0,1 2,12.7 V10.3 A0.8,0.8 0 0,1 2.8,9.5 Z M11,9.5 H13.2 A0.8,0.8 0 0,1 14,10.3 V12.7 A0.8,0.8 0 0,1 13.2,13.5 H11 A0.8,0.8 0 0,1 10.2,12.7 V10.3 A0.8,0.8 0 0,1 11,9.5 Z", 16.0, false),
            // 6. text.bubble (书伴)
            "bubble-lines" => ("M3.5,2.8 H12.5 A1.6,1.6 0 0,1 14.1,4.4 V10.2 A1.6,1.6 0 0,1 12.5,11.8 H7 L4,14.1 V11.8 H3.5 A1.6,1.6 0 0,1 1.9,10.2 V4.4 A1.6,1.6 0 0,1 3.5,2.8 Z M5.2,6.1 H10.8 M5.2,8.6 H9.2", 16.0, false),
            // 7. arrow.triangle.2.circlepath (同步)
            "sync-arrows" => ("M12.8,6.2 A5.1,5.1 0 0,0 4.1,4.7 M3.6,2.6 V5.1 H6.1 M3.2,9.8 A5.1,5.1 0 0,0 11.9,11.3 M12.4,13.4 V10.9 H9.9", 16.0, false),
            // 8. square.and.arrow.up (笔记导出 / 分享)
            "share-up" or "share" => ("M8,2.2 V9.8 M5.4,4.8 L8,2.2 L10.6,4.8 M4.8,6.8 H3.8 A1.4,1.4 0 0,0 2.4,8.2 V12.4 A1.4,1.4 0 0,0 3.8,13.8 H12.2 A1.4,1.4 0 0,0 13.6,12.4 V8.2 A1.4,1.4 0 0,0 12.2,6.8 H11.2", 16.0, false),
            "share-down" => ("M8,2.2 V9.8 M5.4,7.2 L8,9.8 L10.6,7.2 M4.8,6.8 H3.8 A1.4,1.4 0 0,0 2.4,8.2 V12.4 A1.4,1.4 0 0,0 3.8,13.8 H12.2 A1.4,1.4 0 0,0 13.6,12.4 V8.2 A1.4,1.4 0 0,0 12.2,6.8 H11.2", 16.0, false),
            "text-quote" => ("M3.1,5.3 A1.05,1.05 0 1,1 3.09,5.3 Z M2.9,4.4 C3.1,3.2 3.9,2.5 4.9,2.2 M6.5,5.3 A1.05,1.05 0 1,1 6.49,5.3 Z M6.3,4.4 C6.5,3.2 7.3,2.5 8.3,2.2 M9.8,5.1 H13.6 M2.8,8.8 H13.6 M2.8,12.2 H10.4", 16.0, false),
            // 9. internaldrive (存储)
            "drive" => ("M3.6,3.4 L12.4,3.4 L14,8.8 V12.2 A1.2,1.2 0 0,1 12.8,13.4 H3.2 A1.2,1.2 0 0,1 2,12.2 V8.8 Z M2.2,9 H13.8 M5,11.2 H8.5 M11.4,11.2 H11.6", 16.0, false),
            // 10. shippingbox (数据)
            "cube" => ("M8,1.8 L13.6,4.9 V11.1 L8,14.2 L2.4,11.1 V4.9 Z M2.4,4.9 L8,8 L13.6,4.9 M8,8 V14.2", 16.0, false),
            // 11. info.circle (关于)
            "info-circle" => ("M8,2 A6,6 0 1,1 7.99,2 Z M8,7.3 V11.2 M8,4.9 H8.05", 16.0, false),

            // Sync & Reader extra icons
            "cloud" => ("M4.8,12.4 H11.6 A2.7,2.7 0 0,0 12.1,7.1 A3.9,3.9 0 0,0 4.6,6.1 A2.9,2.9 0 0,0 4.8,12.4 Z", 16.0, false),
            "cloud-down" or "icloud.and.arrow.down" => ("M4.6,11.2 H6.2 M9.8,11.2 H11.6 A2.5,2.5 0 0,0 12.1,6.3 A3.7,3.7 0 0,0 4.8,5.3 A2.7,2.7 0 0,0 4.6,11.2 M8,7.8 V13.8 M6,11.8 L8,13.8 L10,11.8", 16.0, false),
            "cloud-slash" => ("M4.8,12.4 H11.4 M12.2,7.2 A3.8,3.8 0 0,0 5.3,5.2 M2.4,2.4 L13.6,13.6 M3.1,7.5 A2.8,2.8 0 0,0 4.8,12.4", 16.0, false),
            "server" => ("M3.5,2.8 H12.5 A1.2,1.2 0 0,1 13.7,4 V6.4 A1.2,1.2 0 0,1 12.5,7.6 H3.5 A1.2,1.2 0 0,1 2.3,6.4 V4 A1.2,1.2 0 0,1 3.5,2.8 Z M8,7.6 V11.8 M4.2,11.8 H11.8 M6.8,10.8 H9.2 V12.8 H6.8 Z", 16.0, false),
            "globe" => ("M8,2 A6,6 0 1,1 7.99,2 Z M2.2,8 H13.8 M8,2 C9.8,3.8 9.8,12.2 8,14 M8,2 C6.2,3.8 6.2,12.2 8,14", 16.0, false),
            "laptop" => ("M3.4,3.6 H12.6 A1,1 0 0,1 13.6,4.6 V10.6 H2.4 V4.6 A1,1 0 0,1 3.4,3.6 Z M1.4,12.2 H14.6", 16.0, false),
            "phone" => ("M5.2,2.2 H10.8 A1.2,1.2 0 0,1 12,3.4 V12.6 A1.2,1.2 0 0,1 10.8,13.8 H5.2 A1.2,1.2 0 0,1 4,12.6 V3.4 A1.2,1.2 0 0,1 5.2,2.2 Z M7.2,11.8 H8.8", 16.0, false),
            "tablet" => ("M3.6,2.2 H12.4 A1.2,1.2 0 0,1 13.6,3.4 V12.6 A1.2,1.2 0 0,1 12.4,13.8 H3.6 A1.2,1.2 0 0,1 2.4,12.6 V3.4 A1.2,1.2 0 0,1 3.6,2.2 Z M7.2,11.9 H8.8", 16.0, false),
            "lock" => ("M4.4,7.4 H11.6 A1,1 0 0,1 12.6,8.4 V12.8 A1,1 0 0,1 11.6,13.8 H4.4 A1,1 0 0,1 3.4,12.8 V8.4 A1,1 0 0,1 4.4,7.4 Z M5.5,7.4 V5.2 A2.5,2.5 0 0,1 10.5,5.2 V7.4", 16.0, false),
            "clock-history" or "history" => ("M3.2,5.2 A5.5,5.5 0 1,1 2.7,8.8 M2.6,2.8 V5.5 H5.3 M8,5.2 V8.2 L10.2,9.5", 16.0, false),
            "checkmark" => ("M3.2,8.4 L6.4,11.6 L12.8,4.6", 16.0, false),
            "folder" => ("M2.2,4.4 A1.2,1.2 0 0,1 3.4,3.2 H6.3 L7.6,4.8 H12.6 A1.2,1.2 0 0,1 13.8,6 V11.8 A1.2,1.2 0 0,1 12.6,13 H3.4 A1.2,1.2 0 0,1 2.2,11.8 Z", 16.0, false),
            "speaker" => ("M2.8,6.2 H5.2 L8.4,3.5 V12.5 L5.2,9.8 H2.8 Z M10.6,5.6 C11.5,6.8 11.5,9.2 10.6,10.4 M12.4,4 C14,6.2 14,9.8 12.4,12", 16.0, false),
            // Exact apple/Yuyue/Assets.xcassets/cast.imageset/cast.svg (speech bubble + 3 soundwave bars)
            "cast" => ("M20.5,11.2 C20.5,15.5 16.7,19 12,19 C10.6,19 9.3,18.7 8.1,18.1 L3.5,19.5 L4.7,15.5 C3.9,14.3 3.5,12.8 3.5,11.2 C3.5,6.9 7.3,3.5 12,3.5 C16.7,3.5 20.5,6.9 20.5,11.2 Z M8.8,9.8 V12.6 M12,8.2 V14.2 M15.2,9.8 V12.6", 24.0, false),
            "arrow-up-right" => ("M4.5,11.5 L11.5,4.5 M6,4.5 H11.5 V10", 16.0, false),

            // Shared UI icons — exact apple/Yuyue/Assets.xcassets/note-card.imageset/note-card.svg & bug.svg
            "note-card" => ("M5.8,3 H18.2 A2.8,2.8 0 0,1 21,5.8 V14.2 L14.2,21 H5.8 A2.8,2.8 0 0,1 3,18.2 V5.8 A2.8,2.8 0 0,1 5.8,3 Z M14.2,21 V15.8 A1.6,1.6 0 0,1 15.8,14.2 H21 M7.5,8.8 H16.5 M7.5,12.8 H13", 24.0, false),
            "bug" => ("M8.5,2.2 L10.1,4.4 M15.5,2.2 L13.9,4.4 M8,7.8 C8,5.3 9.7,3.9 12,3.9 C14.3,3.9 16,5.3 16,7.8 M6,8.5 C6,8.1 6.3,7.8 6.7,7.8 H17.3 C17.7,7.8 18,8.1 18,8.5 V14.8 C18,18.6 15.3,21.5 12,21.5 C8.7,21.5 6,18.6 6,14.8 Z M12,11 V21.2 M2.2,8.2 L6,10.2 M21.8,8.2 L18,10.2 M1.8,14 H6 M22.2,14 H18 M2.6,19.8 L6.6,17.6 M21.4,19.8 L17.4,17.6", 24.0, false),
            "search" => ("M7,2.8 A4.2,4.2 0 1,1 6.99,2.8 Z M10.2,10.2 L13.5,13.5", 16.0, false),
            "sidebar" => ("M3.8,3 H12.2 A1.8,1.8 0 0,1 14,4.8 V11.2 A1.8,1.8 0 0,1 12.2,13 H3.8 A1.8,1.8 0 0,1 2,11.2 V4.8 A1.8,1.8 0 0,1 3.8,3 Z M6,3 V13", 16.0, false),
            "plus" => ("M8,3 V13 M3,8 H13", 16.0, false),
            "chart-bar" => ("M3,13 V8.8 H5.6 V13 M6.7,13 V5.8 H9.3 V13 M10.4,13 V3 H13 V13 M2,13 H14", 16.0, false),
            "minus" => ("M3,8 H13", 16.0, false),
            "updown" => ("M5.2,6.2 L8,3.4 L10.8,6.2 M5.2,9.8 L8,12.6 L10.8,9.8", 16.0, false),
            "sparkle" => ("M8,1.8 C8.4,5.4 10.6,7.6 14.2,8 C10.6,8.4 8.4,10.6 8,14.2 C7.6,10.6 5.4,8.4 1.8,8 C5.4,7.6 7.6,5.4 8,1.8 Z", 16.0, false),
            "feedback" => ("M3.5,3 H12.5 A1.5,1.5 0 0,1 14,4.5 V10.5 A1.5,1.5 0 0,1 12.5,12 H7 L4,14.2 V12 H3.5 A1.5,1.5 0 0,1 2,10.5 V4.5 A1.5,1.5 0 0,1 3.5,3 Z M8,5.5 V8.2 M8,10 H8.05", 16.0, false),
            // Proper SF Symbol gearshape (6-tooth cog + inner ring)
            "gear" => ("M8,5.7 A2.3,2.3 0 1,1 7.99,5.7 Z M6.7,1.9 H9.3 L9.7,3.5 C10.3,3.7 10.9,4.1 11.4,4.5 L13,4 L14.3,6.2 L13.1,7.3 C13.2,7.5 13.2,7.8 13.2,8 C13.2,8.2 13.2,8.5 13.1,8.7 L14.3,9.8 L13,12 L11.4,11.5 C10.9,11.9 10.3,12.3 9.7,12.5 L9.3,14.1 H6.7 L6.3,12.5 C5.7,12.3 5.1,11.9 4.6,11.5 L3,12 L1.7,9.8 L2.9,8.7 C2.8,8.5 2.8,8.2 2.8,8 C2.8,7.8 2.8,7.5 2.9,7.3 L1.7,6.2 L3,4 L4.6,4.5 C5.1,4.1 5.7,3.7 6.3,3.5 Z", 16.0, false),
            // Extra macOS icons (Settings / Podcast / NoteExport / Companion / Reader)
            "folder-plus" => ("M2.2,4.4 A1.2,1.2 0 0,1 3.4,3.2 H6.3 L7.6,4.8 H12.6 A1.2,1.2 0 0,1 13.8,6 V11.8 A1.2,1.2 0 0,1 12.6,13 H3.4 A1.2,1.2 0 0,1 2.2,11.8 Z M8,7.2 V10.8 M6.2,9 H9.8", 16.0, false),
            "arrow-down-circle" => ("M8,2 A6,6 0 1,1 7.99,2 Z M8,5 V10.8 M5.6,8.5 L8,11 L10.4,8.5", 16.0, false),
            "doc-lines" => ("M3,4.2 H13 M3,8 H10.5 M3,11.8 H8", 16.0, false),
            "link-chain" => ("M6.8,9.2 L9.2,6.8 M5.2,10.8 A2.5,2.5 0 0,1 5.2,7.2 L6.8,5.6 A2.5,2.5 0 0,1 10.4,5.6 M10.8,5.2 A2.5,2.5 0 0,1 10.8,8.8 L9.2,10.4 A2.5,2.5 0 0,1 5.6,10.4", 16.0, false),
            "paperclip" => ("M10.8,5.2 L6.1,9.9 A1.4,1.4 0 0,1 4.1,7.9 L8.9,3.1 A2.7,2.7 0 0,1 12.7,6.9 L7.6,12 A4,4 0 0,1 1.9,6.3 L6.6,1.6", 16.0, false),
            "camera" => ("M2.2,5.2 A1.4,1.4 0 0,1 3.6,3.8 H5.1 L6.1,2.5 H9.9 L10.9,3.8 H12.4 A1.4,1.4 0 0,1 13.8,5.2 V11.8 A1.4,1.4 0 0,1 12.4,13.2 H3.6 A1.4,1.4 0 0,1 2.2,11.8 Z M8,6.2 A2.3,2.3 0 1,1 7.99,6.2 Z", 16.0, false),
            "photo" => ("M2.2,3.4 A1.4,1.4 0 0,1 3.6,2 H12.4 A1.4,1.4 0 0,1 13.8,3.4 V12.6 A1.4,1.4 0 0,1 12.4,14 H3.6 A1.4,1.4 0 0,1 2.2,12.6 Z M5.5,5.2 A1.1,1.1 0 1,1 5.49,5.2 Z M2.4,11.6 L5.6,8.4 L8.1,10.9 L10.6,8.2 L13.6,11.6", 16.0, false),
            "circles-venn" => ("M6.2,5.3 A2.7,2.7 0 1,1 6.19,5.3 Z M9.8,5.3 A2.7,2.7 0 1,1 9.79,5.3 Z", 16.0, false),
            "checklist-lines" => ("M3,4.8 H12.5 M3,8 H9 M3,11.2 H7.5 M11.4,9.2 A1.7,1.7 0 1,1 11.39,9.2 Z", 16.0, false),
            "waveform-ecg" => ("M1.5,8 H4.2 L5.8,3.8 L8.1,12.2 L10.1,6.2 L11.5,8 H14.5", 16.0, false),
            "obsidian" => ("M8.4,1.6 L12.8,4.6 L13.8,10.4 L9.2,14.4 L4.2,12.4 L2.6,6.4 Z M8.4,1.6 L6.8,7.4 L9.2,14.4 M12.8,4.6 L6.8,7.4 L2.6,6.4", 16.0, false),
            "notion" => ("M3.4,2.8 H12.6 A1,1 0 0,1 13.6,3.8 V12.2 A1,1 0 0,1 12.6,13.2 H3.4 A1,1 0 0,1 2.4,12.2 V3.8 A1,1 0 0,1 3.4,2.8 Z M5.5,11 V5 L10.5,11 V5", 16.0, false),
            "flomo" => ("M5.8,12.8 V6.4 A2.6,2.6 0 0,1 8.4,3.8 H12.2 M4.2,7.8 H10.2 M10.6,6.4 A1.4,1.4 0 1,1 10.59,6.4 Z", 16.0, false),
            "xinzhi" => ("M8,1.8 L12.4,5.6 L11.2,12.6 L8,14.2 L4.8,12.6 L3.6,5.6 Z M8,1.8 V14.2", 16.0, false),
            "claude" => ("M8,2 V14 M2,8 H14 M3.8,3.8 L12.2,12.2 M12.2,3.8 L3.8,12.2", 16.0, false),
            "openai" => ("M8,2.2 A2.8,2.8 0 0,1 12.2,5.6 A2.8,2.8 0 0,1 12.2,10.4 A2.8,2.8 0 0,1 8,13.8 A2.8,2.8 0 0,1 3.8,10.4 A2.8,2.8 0 0,1 3.8,5.6 A2.8,2.8 0 0,1 8,2.2 Z M8,5.8 A2.2,2.2 0 1,1 7.99,5.8 Z", 16.0, false),
            "deepseek" => ("M2.4,9.2 C3.8,5.4 8.2,4.4 11.4,6.4 C12.4,7 13.4,6.2 14,5.4 C13.8,7.6 12.8,9.8 10.2,11.2 C7.2,12.6 3.6,11.6 2.4,9.2 Z M7.8,7.6 A0.6,0.6 0 1,1 7.79,7.6 Z", 16.0, false),
            "gemini" => ("M8,1.6 C8.6,5.6 10.4,7.4 14.4,8 C10.4,8.6 8.6,10.4 8,14.4 C7.4,10.4 5.6,8.6 1.6,8 C5.6,7.4 7.4,5.6 8,1.6 Z", 16.0, true),
            "openrouter" => ("M2.4,8 A3.4,3.4 0 0,1 9,6.6 H13.6 M11.4,4.4 L13.6,6.6 L11.4,8.8 M2.4,8 A3.4,3.4 0 0,0 9,9.4 H13.6", 16.0, false),
            "list" => ("M5.5,4.2 H13.5 M5.5,8 H13.5 M5.5,11.8 H13.5 M2.5,4.2 H3 M2.5,8 H3 M2.5,11.8 H3", 16.0, false),
            "bookmark" => ("M4.5,2.5 H11.5 V13.5 L8,11 L4.5,13.5 Z", 16.0, filled),
            "play" => ("M5.2,3.2 L13,8 L5.2,12.8 Z", 16.0, true),
            "pause" => ("M4.6,3.2 H7 V12.8 H4.6 Z M9,3.2 H11.4 V12.8 H9 Z", 16.0, true),
            "stop" => ("M4.2,4.2 H11.8 V11.8 H4.2 Z", 16.0, true),
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
            "more" or "ellipsis" => ("M3.6,8 A1.15,1.15 0 1,1 3.59,8 Z M8,8 A1.15,1.15 0 1,1 7.99,8 Z M12.4,8 A1.15,1.15 0 1,1 12.39,8 Z", 16.0, true),

            // macOS CollectionIcon SF Symbols (Shelf.swift 1:1) & cross-device aliases
            "books.vertical" => ("M2.8,3.6 H5.4 V13.2 H2.8 Z M6.6,2.8 H9.2 V13.2 H6.6 Z M10.6,4 L12.9,3.4 L14.4,12.8 L12.1,13.4 Z", 16.0, false),
            "book.closed" => ("M6.5,3.5 H17 A1,1 0 0,1 18,4.5 V19.5 A1,1 0 0,1 17,20.5 H6.5 A1.5,1.5 0 0,1 5,19 V5 A1.5,1.5 0 0,1 6.5,3.5 Z M5,17.5 H18 M14,3.5 V10.5 L12.25,9.1 L10.5,10.5 V3.5", 24.0, false),
            "moon.stars" => ("M11.6,10.4 A4.8,4.8 0 0,1 5.6,4.4 A4.9,4.9 0 1,0 11.6,10.4 Z", 16.0, filled),
            "sparkles" => ("M8,1.8 C8.4,5.4 10.6,7.6 14.2,8 C10.6,8.4 8.4,10.6 8,14.2 C7.6,10.6 5.4,8.4 1.8,8 C5.4,7.6 7.6,5.4 8,1.8 Z", 16.0, false),
            "sun.max" => ("M8,5.2 A2.8,2.8 0 1,1 7.99,5.2 Z M8,1.5 V2.9 M8,13.1 V14.5 M1.5,8 H2.9 M13.1,8 H14.5 M3.4,3.4 L4.4,4.4 M11.6,11.6 L12.6,12.6 M12.6,3.4 L11.6,4.4 M4.4,11.6 L3.4,12.6", 16.0, false),
            "arrow.trianglehead.2.clockwise" => ("M12.8,6.2 A5.1,5.1 0 0,0 4.1,4.7 M3.6,2.6 V5.1 H6.1 M3.2,9.8 A5.1,5.1 0 0,0 11.9,11.3 M12.4,13.4 V10.9 H9.9", 16.0, false),
            "cup.and.saucer" or "coffee" => ("M3,5.2 H11 V9.5 A2.8,2.8 0 0,1 8.2,12.3 H5.8 A2.8,2.8 0 0,1 3,9.5 Z M11,6.2 H12.4 A1.4,1.4 0 0,1 12.4,9 H11 M2.4,13.6 H11.8", 16.0, false),
            "key" => ("M5.6,8 A2.6,2.6 0 1,1 5.59,8 Z M8.2,8 H14 M11.6,8 V10.2 M13.6,8 V9.8", 16.0, false),
            "quote.opening" => ("M3.4,9.2 A1.8,1.8 0 1,1 3.39,9.2 Z M3.2,7.6 C3.4,5.4 4.8,4 6.8,3.4 M9.6,9.2 A1.8,1.8 0 1,1 9.59,9.2 Z M9.4,7.6 C9.6,5.4 11,4 13,3.4", 16.0, false),
            "eye" => ("M2.2,8 C3.8,5.1 5.8,4 8,4 C10.2,4 12.2,5.1 13.8,8 C12.2,10.9 10.2,12 8,12 C5.8,12 3.8,10.9 2.2,8 Z M8,6.3 A1.7,1.7 0 1,1 7.99,6.3 Z", 16.0, false),
            "leaf" => ("M13.2,2.8 C8,2.8 3.6,6 3.2,12.8 C10,12.4 13.2,8 13.2,2.8 Z M3.2,12.8 L9.2,6.8", 16.0, false),
            "star" => ("M8,2.2 L9.7,5.8 L13.7,6.3 L10.8,9 L11.5,13 L8,11.1 L4.5,13 L5.2,9 L2.3,6.3 L6.3,5.8 Z", 16.0, filled),
            "flame" => ("M8.2,2.2 C10.6,4.8 12.4,7.1 12.4,9.5 A4.4,4.4 0 1,1 3.6,9.5 C3.6,7.6 4.7,6.1 5.8,5.2 C5.8,6.6 6.7,7.3 7.3,6.6 C7.9,5.8 7.6,3.8 8.2,2.2 Z", 16.0, false),
            "tag" => ("M2.8,7.8 L7.8,2.8 H13.2 V8.2 L8.2,13.2 Z M10.4,5.6 A0.7,0.7 0 1,1 10.39,5.6 Z", 16.0, false),
            "music" or "music.note" => ("M6.4,11.4 V3.8 L13,2.6 V10.2 M6.4,11.4 A1.6,1.3 0 1,1 6.39,11.4 Z M13,10.2 A1.6,1.3 0 1,1 12.99,10.2 Z", 16.0, false),
            "film" => ("M2.4,3.4 H13.6 V12.6 H2.4 Z M5.2,3.4 V12.6 M10.8,3.4 V12.6 M2.4,6.4 H5.2 M2.4,9.6 H5.2 M10.8,6.4 H13.6 M10.8,9.6 H13.6", 16.0, false),
            "compass" => ("M8,2 A6,6 0 1,1 7.99,2 Z M10.4,5.6 L8.8,8.8 L5.6,10.4 L7.2,7.2 Z", 16.0, false),
            "lamp" => ("M4.5,3.2 H11.5 L13,8 H3 Z M8,8 V13.2 M5.2,13.2 H10.8", 16.0, false),

            // Windows 11 Native Fluent 2 workspace icons
            "grid-view" => ("M2.5,2.5 H6.8 V6.8 H2.5 Z M9.2,2.5 H13.5 V6.8 H9.2 Z M2.5,9.2 H6.8 V13.5 H2.5 Z M9.2,9.2 H13.5 V13.5 H9.2 Z", 16.0, false),
            "list-view" => ("M2.4,4.2 H4.4 M6.4,4.2 H13.6 M2.4,8 H4.4 M6.4,8 H13.6 M2.4,11.8 H4.4 M6.4,11.8 H13.6", 16.0, false),
            "split-view" => ("M2.4,3.2 H13.6 A1.2,1.2 0 0,1 14.8,4.4 V11.6 A1.2,1.2 0 0,1 13.6,12.8 H2.4 A1.2,1.2 0 0,1 1.2,11.6 V4.4 A1.2,1.2 0 0,1 2.4,3.2 Z M8,3.2 V12.8", 16.0, false),
            "pip" => ("M2.4,3 H13.6 A1.2,1.2 0 0,1 14.8,4.2 V11.8 A1.2,1.2 0 0,1 13.6,13 H2.4 A1.2,1.2 0 0,1 1.2,11.8 V4.2 A1.2,1.2 0 0,1 2.4,3 Z M8.2,7.8 H13.2 V11.6 H8.2 Z", 16.0, false),
            "panel-left" => ("M3.2,2.8 H12.8 A1.4,1.4 0 0,1 14.2,4.2 V11.8 A1.4,1.4 0 0,1 12.8,13.2 H3.2 A1.4,1.4 0 0,1 1.8,11.8 V4.2 A1.4,1.4 0 0,1 3.2,2.8 Z M5.8,2.8 V13.2", 16.0, false),
            "panel-right" => ("M3.2,2.8 H12.8 A1.4,1.4 0 0,1 14.2,4.2 V11.8 A1.4,1.4 0 0,1 12.8,13.2 H3.2 A1.4,1.4 0 0,1 1.8,11.8 V4.2 A1.4,1.4 0 0,1 3.2,2.8 Z M10.2,2.8 V13.2", 16.0, false),
            "fullscreen" => ("M2.5,5.8 V2.5 H5.8 M10.2,2.5 H13.5 V5.8 M13.5,10.2 V13.5 H10.2 M5.8,13.5 H2.5 V10.2", 16.0, false),
            "arrow-left" => ("M13,8 H3.4 M7.2,4.2 L3.4,8 L7.2,11.8", 16.0, false),
            "arrow-right" => ("M3,8 H12.6 M8.8,4.2 L12.6,8 L8.8,11.8", 16.0, false),
            "wifi" => ("M2.4,6.4 C5.4,3.6 10.6,3.6 13.6,6.4 M4.6,8.8 C6.5,7 9.5,7 11.4,8.8 M6.8,11.2 C7.5,10.5 8.5,10.5 9.2,11.2 M8,13.2 A0.6,0.6 0 1,1 7.99,13.2 Z", 16.0, false),
            "keyboard" => ("M2.2,4.2 H13.8 A1.2,1.2 0 0,1 15,5.4 V11.2 A1.2,1.2 0 0,1 13.8,12.4 H2.2 A1.2,1.2 0 0,1 1,11.2 V5.4 A1.2,1.2 0 0,1 2.2,4.2 Z M3.8,6.8 H4.8 M6.8,6.8 H7.8 M9.8,6.8 H10.8 M12.2,6.8 H12.8 M5,9.8 H11", 16.0, false),
            "filter" => ("M2.4,3.4 H13.6 L9.4,8.2 V12.6 L6.6,11.2 V8.2 Z", 16.0, false),
            "sort-asc" => ("M4.5,12.5 V3.5 M2.2,5.8 L4.5,3.5 L6.8,5.8 M9.2,5 H13.8 M9.2,8 H12.6 M9.2,11 H11.2", 16.0, false),
            "sort-desc" => ("M4.5,3.5 V12.5 M2.2,10.2 L4.5,12.5 L6.8,10.2 M9.2,5 H13.8 M9.2,8 H12.6 M9.2,11 H11.2", 16.0, false),
            "detach" => ("M9,2.5 H13.5 V7 M13.5,2.5 L7.5,8.5 M12.5,9.5 V12.5 A1,1 0 0,1 11.5,13.5 H3.5 A1,1 0 0,1 2.5,12.5 V4.5 A1,1 0 0,1 3.5,3.5 H6.5", 16.0, false),
            "restore-tab" => ("M3.2,5.6 A5.2,5.2 0 1,1 3.4,10.8 M3,2.8 V5.8 H6 M7.5,6.2 V9.4 H10.2", 16.0, false),
            "nav-toggle" => ("M2.5,4.2 H13.5 M2.5,8 H13.5 M2.5,11.8 H13.5", 16.0, false),
            _ => ("M8,2 A6,6 0 1,1 7.99,2 Z", 16.0, false),
        };

        var geometry = ParseGeometry(data);
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            // Stretch.None preserves the exact 0..16 / 0..24 SVG viewBox coordinates inside the viewBox Grid!
            Stretch = Stretch.None,
            StrokeThickness = viewBox > 20 ? 1.75 : (name == "a-wen" ? 1.25 : 1.45),
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
    /// 1:1 <c>Shelf.swift (CollectionIcon.choices)</c> — uses identical SF Symbol identifiers stored in shared SQLite so macOS/iOS/Windows stay synced.
    /// </summary>
    public static readonly (string Symbol, string Label)[] CollectionIconChoices =
    [
        ("books.vertical", "书"),
        ("moon", "睡前"),
        ("headphones", "听"),
        ("sparkle", "科幻"),
        ("globe", "外文"),
        ("cup.and.saucer", "闲读"),
        ("key", "推理"),
        ("quote.opening", "诗词"),
        ("sun.max", "孩子"),
        ("arrow.trianglehead.2.clockwise", "重读"),
        ("heart", "最爱"),
        ("clock", "历史"),
        ("eye", "科普"),
        ("pencil", "写作"),
    ];

    /// <summary>
    /// Ported 1:1 from <c>Shelf.swift (CollectionIcon.symbol)</c>: infers a semantic glyph from collection name.
    /// </summary>
    public static string CollectionIconName(string name, string? customIcon = null)
    {
        if (!string.IsNullOrWhiteSpace(customIcon)) return customIcon;
        string n = name.ToLowerInvariant();
        if (n.Contains('睡') || n.Contains('夜') || n.Contains('晚') || n.Contains("sleep") || n.Contains("night") || n.Contains("bed")) return "moon";
        if (n.Contains("科幻") || n.Contains("宇宙") || n.Contains('星') || n.Contains("sci-fi") || n.Contains("space") || n.Contains("star")) return "sparkle";
        if (n.Contains('听') || n.Contains("通勤") || n.Contains("路上") || n.Contains("有声") || n.Contains("listen") || n.Contains("commute") || n.Contains("audio")) return "headphones";
        if (n.Contains("英文") || n.Contains("原著") || n.Contains("外文") || n.Contains("日文") || n.Contains("english")) return "globe";
        if (n.Contains("周末") || n.Contains("咖啡") || n.Contains('闲') || n.Contains("weekend") || n.Contains("coffee")) return "cup.and.saucer";
        if (n.Contains("推理") || n.Contains("悬疑") || n.Contains("侦探") || n.Contains("mystery") || n.Contains("detective") || n.Contains("crime")) return "key";
        if (n.Contains('诗') || n.Contains('词') || n.Contains("poem") || n.Contains("poetry")) return "quote.opening";
        if (n.Contains("孩子") || n.Contains("儿童") || n.Contains('童') || n.Contains("kid") || n.Contains("child")) return "sun.max";
        if (n.Contains("重读") || n.Contains("再读") || n.Contains("reread")) return "arrow.trianglehead.2.clockwise";
        if (n.Contains("top") || n.Contains('榜') || n.Contains("最爱") || n.Contains("喜欢") || n.Contains("favorite") || n.Contains("love")) return "heart";
        if (n.Contains("历史") || n.Contains('史') || n.Contains("history")) return "clock";
        if (n.Contains("科普") || n.Contains("科学") || n.Contains("science")) return "eye";
        if (n.Contains('写') || n.Contains("作家") || n.Contains("随笔") || n.Contains("writer") || n.Contains("writing")) return "pencil";
        if (n.Contains("文学") || n.Contains("经典")) return "book";
        return "books.vertical";
    }
}

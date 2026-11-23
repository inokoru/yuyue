using System.Text;

namespace Yuyue.Core;

/// <summary>
/// Exports highlights, thoughts and vocab to portable Markdown or Anki TSV/CSV.
/// </summary>
public static class NoteExporter
{
    public static string BuildBookMarkdown(LibraryService library, BookSummary book)
    {
        var marks = library.GetMarks(book.Id);
        var detail = library.GetBookDetail(book.Id);

        var sb = new StringBuilder();
        sb.AppendLine($"# {book.Title}");
        if (book.Authors.Length > 0) sb.AppendLine($"\n> 作者：{string.Join(" / ", book.Authors)}");
        sb.AppendLine($"> 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm} · 共 {marks.Count} 条划线\n");

        foreach (var group in marks.GroupBy(m => m.Section).OrderBy(g => g.Key))
        {
            string chapter = group.Key < detail.Sections.Length && !string.IsNullOrWhiteSpace(detail.Sections[group.Key].Title)
                ? detail.Sections[group.Key].Title!
                : $"第 {group.Key + 1} 节";
            sb.AppendLine($"## {chapter}\n");

            foreach (var m in group)
            {
                sb.AppendLine($"> {m.Text.Replace("\n", "\n> ")}");
                if (!string.IsNullOrWhiteSpace(m.Note))
                {
                    sb.AppendLine($"\n{m.Note}");
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    public static string BuildLibraryMarkdown(LibraryService library)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 语阅笔记汇总\n\n> 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm}\n");

        int booksExported = 0;
        foreach (var book in library.GetKeptBooks())
        {
            var marks = library.GetMarks(book.Id);
            if (marks.Count == 0) continue;
            booksExported++;
            sb.AppendLine("---\n");
            sb.AppendLine(BuildBookMarkdown(library, book));
        }

        if (booksExported == 0)
        {
            sb.AppendLine("（当前书库还没有划线或想法）");
        }
        return sb.ToString();
    }

    public static string BuildAnkiCsv(LibraryService library)
    {
        static string Csv(string? s)
        {
            var v = (s ?? "").Replace("\"", "\"\"");
            return $"\"{v}\"";
        }

        var sb = new StringBuilder();
        sb.AppendLine("Front,Back,Tags");

        foreach (var w in library.GetWords())
        {
            var front = string.IsNullOrEmpty(w.Phonetic) ? w.Word : $"{w.Word} {w.Phonetic}";
            var back = string.IsNullOrEmpty(w.Context) ? (w.Meaning ?? "") : $"{w.Meaning}\n\n{w.Context}";
            sb.AppendLine($"{Csv(front)},{Csv(back)},yuyue-vocab");
        }

        foreach (var book in library.GetKeptBooks())
        {
            foreach (var m in library.GetMarks(book.Id))
            {
                if (string.IsNullOrWhiteSpace(m.Note)) continue;
                sb.AppendLine($"{Csv(m.Text)},{Csv(m.Note)},{Csv($"yuyue-note {book.Title}")}");
            }
        }
        return sb.ToString();
    }
}

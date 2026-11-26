using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Dashboard.Web.Services;

/// <summary>Tiny, safe Markdown → HTML renderer for the digests we generate ourselves: #/##/### headings, - / * lists,
/// paragraphs, **bold**, `code`. Everything is HTML-encoded first, so input can never inject markup.</summary>
public static partial class MiniMarkdown
{
    public static string ToHtml(string? md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        var sb = new StringBuilder();
        var inList = false;
        var para = new List<string>();
        void FlushPara() { if (para.Count > 0) { sb.Append("<p>").Append(string.Join(" ", para)).Append("</p>\n"); para.Clear(); } }
        void CloseList() { if (inList) { sb.Append("</ul>\n"); inList = false; } }

        foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { FlushPara(); CloseList(); continue; }
            var h = Heading().Match(line);
            if (h.Success)
            {
                FlushPara(); CloseList();
                var level = Math.Min(h.Groups[1].Value.Length, 4);
                sb.Append($"<h{level}>").Append(Inline(h.Groups[2].Value)).Append($"</h{level}>\n");
                continue;
            }
            var li = ListItem().Match(line);
            if (li.Success)
            {
                FlushPara();
                if (!inList) { sb.Append("<ul>\n"); inList = true; }
                sb.Append("<li>").Append(Inline(li.Groups[1].Value)).Append("</li>\n");
                continue;
            }
            CloseList();
            para.Add(Inline(line.Trim()));
        }
        FlushPara(); CloseList();
        return sb.ToString();
    }

    private static string Inline(string s)
    {
        var enc = WebUtility.HtmlEncode(s);
        enc = Code().Replace(enc, "<code>$1</code>");
        enc = Bold().Replace(enc, "<strong>$1</strong>");
        return enc;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex Heading();
    [GeneratedRegex(@"^\s*[-*]\s+(.*)$")] private static partial Regex ListItem();
    [GeneratedRegex(@"`([^`]+)`")] private static partial Regex Code();
    [GeneratedRegex(@"\*\*([^*]+)\*\*")] private static partial Regex Bold();
}

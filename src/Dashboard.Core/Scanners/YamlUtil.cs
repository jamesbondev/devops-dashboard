using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Dashboard.Core.Scanners;

/// <summary>Small helpers over the YamlDotNet representation model. Never throw on shape mismatches.</summary>
internal static partial class YamlUtil
{
    /// <summary>Maximum nodes a recursive walk visits (guards against alias "billion laughs" graphs).</summary>
    public const int MaxWalkNodes = 200_000;

    [GeneratedRegex(@"^---(?:[ \t].*)?\r?$", RegexOptions.Multiline)]
    private static partial Regex DocSeparator();

    /// <summary>
    /// Parses every YAML document in <paramref name="text"/>. Documents are split on "---" lines and parsed
    /// individually, so one malformed document does not hide the others. Errors become warnings.
    /// </summary>
    public static List<YamlNode> LoadDocuments(string text, string path, ScanContext ctx)
    {
        var result = new List<YamlNode>();
        var docs = new List<string>();
        var matches = DocSeparator().Matches(text);
        if (matches.Count == 0) docs.Add(text);
        else
        {
            var last = 0;
            foreach (Match m in matches)
            {
                docs.Add(text[last..m.Index]);
                last = m.Index + m.Length;
            }
            docs.Add(text[last..]);
        }

        foreach (var doc in docs)
        {
            if (string.IsNullOrWhiteSpace(doc)) continue;
            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(doc));
                foreach (var d in stream.Documents)
                    if (d.RootNode is not null) result.Add(d.RootNode);
            }
            catch (Exception ex)
            {
                ctx.Warn($"YAML parse error in '{path}': {FirstLine(ex.Message)}");
            }
        }
        return result;
    }

    public static YamlNode? LoadFirst(string text, string path, ScanContext ctx)
        => LoadDocuments(text, path, ctx).FirstOrDefault();

    private static string FirstLine(string s)
    {
        var i = s.IndexOf('\n');
        return i < 0 ? s : s[..i];
    }

    public static YamlNode? Get(this YamlNode? node, string key)
    {
        if (node is not YamlMappingNode map) return null;
        foreach (var kv in map.Children)
            if (kv.Key is YamlScalarNode k && string.Equals(k.Value, key, StringComparison.Ordinal))
                return kv.Value;
        return null;
    }

    public static YamlMappingNode? Map(this YamlNode? node, string key) => node.Get(key) as YamlMappingNode;

    public static IEnumerable<YamlNode> Seq(this YamlNode? node, string key)
        => node.Get(key) is YamlSequenceNode s ? s.Children : [];

    public static string? Str(this YamlNode? node, string key) => node.Get(key).Scalar();

    public static string? Scalar(this YamlNode? node)
    {
        if (node is not YamlScalarNode s) return null;
        if (s.Value is null) return null;
        // Plain "~" / "null" scalars mean null in YAML.
        if (s.Style == YamlDotNet.Core.ScalarStyle.Plain && (s.Value is "~" or "null" or "Null" or "NULL" or "")) return null;
        return s.Value;
    }

    public static bool IsNullish(this YamlNode? node)
        => node is null || (node is YamlScalarNode && node.Scalar() is null);

    public static bool IsNonEmptyMap(this YamlNode? node) => node is YamlMappingNode m && m.Children.Count > 0;

    public static IEnumerable<string> Keys(this YamlNode? node)
        => node is YamlMappingNode m ? m.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value ?? "") : [];

    /// <summary>Depth-first walk over all key/value pairs of all mappings (bounded).</summary>
    public static IEnumerable<(string Key, YamlNode Value)> WalkPairs(YamlNode root)
    {
        var budget = MaxWalkNodes;
        var stack = new Stack<(YamlNode Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && budget-- > 0)
        {
            var (node, depth) = stack.Pop();
            if (depth > 64) continue;
            switch (node)
            {
                case YamlMappingNode m:
                    var pairs = m.Children.ToList();
                    for (var i = pairs.Count - 1; i >= 0; i--) stack.Push((pairs[i].Value, depth + 1));
                    foreach (var kv in pairs)
                        if (kv.Key is YamlScalarNode k && k.Value is not null)
                            yield return (k.Value, kv.Value);
                    break;
                case YamlSequenceNode s:
                    for (var i = s.Children.Count - 1; i >= 0; i--) stack.Push((s.Children[i], depth + 1));
                    break;
            }
        }
    }
}

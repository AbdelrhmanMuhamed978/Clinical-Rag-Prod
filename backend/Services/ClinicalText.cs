using System.Text;
using System.Text.RegularExpressions;

namespace ClinicalRag.Api.Services;

/// <summary>Pinned Unicode identity and conservative, cross-language input syntax.</summary>
public static class ClinicalText
{
    private static readonly IReadOnlySet<int> Whitespace = RuleAssets.Text.GetProperty("whitespace").EnumerateArray().Select(n => n.GetInt32()).ToHashSet();
    private static readonly int[][] Excluded = RuleAssets.Text.GetProperty("excluded_name_ranges").EnumerateArray()
        .Select(row => row.EnumerateArray().Select(n => n.GetInt32()).ToArray()).ToArray();
    private static readonly IReadOnlyDictionary<int, string> Fold = BuildFold();
    private static IReadOnlyDictionary<int, string> BuildFold()
    {
        var result = RuleAssets.Text.GetProperty("casefold_expansions").EnumerateObject().ToDictionary(p => int.Parse(p.Name), p => p.Value.GetString()!);
        foreach (var row in RuleAssets.Text.GetProperty("casefold_ranges").EnumerateArray())
            for (var n = row[0].GetInt32(); n <= row[1].GetInt32(); n += row[2].GetInt32()) result[n] = char.ConvertFromUtf32(n + row[3].GetInt32());
        return result;
    }
    private static bool ExcludedNameChar(int value)
    {
        var left = 0; var right = Excluded.Length - 1;
        while (left <= right)
        {
            var mid = (left + right) / 2;
            if (value < Excluded[mid][0]) right = mid - 1;
            else if (value > Excluded[mid][1]) left = mid + 1;
            else return true;
        }
        return false;
    }
    private static bool ValidUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
            if (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i]))) return false;
        return true;
    }
    public static bool HasText(string value) => value.EnumerateRunes().Any(rune => !Whitespace.Contains(rune.Value));
    public static bool ValidName(string value) => value.Length <= 128 && ValidUtf16(value) && HasText(value)
        && !value.EnumerateRunes().Any(rune => ExcludedNameChar(rune.Value));
    public static string Alias(string value)
    {
        var output = new StringBuilder(); var space = false;
        foreach (var rune in value.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (Whitespace.Contains(rune.Value)) { space = output.Length != 0; continue; }
            if (space) output.Append(' ');
            space = false;
            output.Append(Fold.TryGetValue(rune.Value, out var folded) ? folded : rune.ToString());
        }
        return output.ToString();
    }
    private static bool FullMatch(string value, string pattern) => Regex.IsMatch(value, @"\A(?:" + pattern + @")\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static bool Ipv4(string host)
    {
        var parts = host.Split('.');
        return parts.Length == 4 && parts.All(part => FullMatch(part, "0|[1-9][0-9]{0,2}") && int.Parse(part) <= 255);
    }
    private static bool Ipv6(string host)
    {
        if (host.Contains('.'))
        {
            var tail = host[(host.LastIndexOf(':') + 1)..];
            if (!Ipv4(tail)) return false;
            host = host[..^tail.Length] + "0:0";
        }
        var halves = host.Split("::");
        if (halves.Length > 2) return false;
        var parts = halves.Where(half => half.Length > 0).SelectMany(half => half.Split(':')).ToArray();
        return (halves.Length == 2 ? parts.Length < 8 : parts.Length == 8) && parts.All(part => FullMatch(part, "[0-9A-Fa-f]{1,4}"));
    }
    public static bool HttpSourceUrl(string value)
    {
        if (value.Length is 0 or > 2048 || !ValidUtf16(value) || value.EnumerateRunes().Any(r => Whitespace.Contains(r.Value) || ExcludedNameChar(r.Value))
            || value.Contains('\\') || Regex.IsMatch(value, "%(?![0-9A-Fa-f]{2})", RegexOptions.CultureInvariant)) return false;
        var match = Regex.Match(value, @"\A[Hh][Tt][Tt][Pp][Ss]?://([^/?#]+)", RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        var authority = match.Groups[1].Value;
        if (authority.Contains('@') || authority.Contains('%')) return false;
        Match hostMatch;
        if (authority.StartsWith('['))
        {
            hostMatch = Regex.Match(authority, @"\A\[([^\]]+)\](?::([0-9]{1,5}))?\z", RegexOptions.CultureInvariant);
            if (!hostMatch.Success || !Ipv6(hostMatch.Groups[1].Value)) return false;
        }
        else
        {
            hostMatch = Regex.Match(authority, @"\A([^:]+)(?::([0-9]{1,5}))?\z", RegexOptions.CultureInvariant);
            if (!hostMatch.Success) return false;
            var host = hostMatch.Groups[1].Value;
            if (host.EndsWith('.')) host = host[..^1];
            var labels = host.Split('.');
            if (host.Length > 253 || labels.Any(label => !FullMatch(label, "[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?"))
                || FullMatch(labels[^1], "[0-9]+|0[xX][0-9A-Fa-f]+") && !Ipv4(host)) return false;
        }
        return !hostMatch.Groups[2].Success || int.Parse(hostMatch.Groups[2].Value) <= 65535;
    }
    public static string? PortableRegexProblem(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        if (pattern.Length > 512) return "Link terms must be a regular expression of at most 512 characters.";
        const string invalid = "Use portable groups, classes, escapes and quantifiers (repeat bounds up to 10000); escape literal brackets and braces.";
        const string escapes = @"bBdDsSwWnrtfv\.*+?^$[]{}()|/-";
        var groups = 0; var inClass = false; var content = false; var repeatable = false; var classSet = false; var classRange = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\')
            {
                if (++i == pattern.Length || !escapes.Contains(pattern[i]) || inClass && "bB".Contains(pattern[i])) return invalid;
                if (inClass && classRange && "dDsSwW".Contains(pattern[i])) return invalid;
                classSet = inClass && "dDsSwW".Contains(pattern[i]); classRange = false;
                content = true; repeatable = !"bB".Contains(pattern[i]);
            }
            else if (inClass)
            {
                if (c == '[' || i + 1 < pattern.Length && new[] { "&&", "~~", "||", "--" }.Contains(pattern.Substring(i, 2))) return invalid;
                if (c == ']') { if (!content) return invalid; inClass = false; repeatable = true; }
                else
                {
                    var range = c == '-' && content && (i + 1 == pattern.Length || pattern[i + 1] != ']');
                    if (range && classSet) return invalid; // A shorthand set cannot be a range endpoint in Python.
                    classRange = range; classSet = false; content = true;
                }
            }
            else if (c == '[') { inClass = true; content = false; classSet = false; classRange = false; if (i + 1 < pattern.Length && pattern[i + 1] == '^') i++; }
            else if (c == '(')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '?') { if (i + 2 == pattern.Length || pattern[i + 2] != ':') return invalid; i += 2; }
                groups++; repeatable = false;
            }
            else if (c == ')') { if (groups == 0) return invalid; groups--; repeatable = true; }
            else if ("|^$".Contains(c)) repeatable = false;
            else if ("*+?{".Contains(c))
            {
                if (!repeatable) return invalid;
                if (c == '{')
                {
                    var end = pattern.IndexOf('}', i + 1);
                    if (end < 0) return invalid;
                    var count = Regex.Match(pattern[(i + 1)..end], @"\A([0-9]{1,5})(?:,([0-9]{0,5}))?\z", RegexOptions.CultureInvariant);
                    if (!count.Success) return invalid;
                    var lower = int.Parse(count.Groups[1].Value);
                    int? upper = count.Groups[2].Length > 0 ? int.Parse(count.Groups[2].Value) : null;
                    if (lower > 10000 || upper != null && (upper > 10000 || upper < lower)) return invalid;
                    i = end;
                }
                if (i + 1 < pattern.Length && pattern[i + 1] == '?') i++;
                repeatable = false;
            }
            else if ("]}".Contains(c)) return invalid;
            else repeatable = true;
        }
        return groups != 0 || inClass ? invalid : null;
    }
}

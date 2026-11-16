using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GhostCode.Core.Models;

namespace GhostCode.Core.Analysis;

public static class SourceAnalyzer
{
    private static readonly string[] BoilerplatePrefixes =
    {
        "#include", "using namespace", "int main", "void main", "return 0;",
        "import ", "from ", "if __name__"
    };

    private static readonly string[] CppMarkers =
    {
        "#include <iostream>", "#include<iostream>", "bits/stdc++.h",
        "using namespace std", "std::", "cout", "cin >>", "cin>>", "#include <vector>"
    };

    public static SourceLanguage? LanguageFromPath(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "c" => SourceLanguage.C,
            "cpp" or "cc" or "cxx" or "c++" => SourceLanguage.Cpp,
            "h" or "hpp" or "hh" => SourceLanguage.Header,
            "py" or "pyw" => SourceLanguage.Python,
            _ => null
        };
    }

    public static string DefaultExtension(SourceLanguage lang) => lang switch
    {
        SourceLanguage.C => "c",
        SourceLanguage.Cpp => "cpp",
        SourceLanguage.Header => "h",
        SourceLanguage.Python => "py",
        _ => "txt"
    };

    public static bool IsEligibleSourceFile(string path, IEnumerable<string>? excludedDirs = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.') || name.StartsWith('~') ||
            name.Contains(".tmp.", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (LanguageFromPath(path) is null)
            return false;

        var normalized = Path.GetFullPath(path);
        string[] systemExcludes = { @"\.git\", @"\node_modules\", @"\bin\", @"\obj\", @"\target\", @"\.vs\", @"\CodeArchive\", @"\GhostCodeArchive\", @"\CodePTIT_Ready\" };
        foreach (var ex in systemExcludes)
        {
            if (normalized.Contains(ex, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (excludedDirs is not null)
        {
            foreach (var dir in excludedDirs)
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                var fullDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (normalized.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        return true;
    }

    public static (string Text, string EncodingName) DecodePreservingInfo(byte[] rawBytes)
    {
        if (rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(rawBytes, 3, rawBytes.Length - 3), "UTF-8-BOM");
        if (rawBytes.Length >= 2 && rawBytes[0] == 0xFF && rawBytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(rawBytes, 2, rawBytes.Length - 2), "UTF-16-LE");
        if (rawBytes.Length >= 2 && rawBytes[0] == 0xFE && rawBytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(rawBytes, 2, rawBytes.Length - 2), "UTF-16-BE");

        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            return (strictUtf8.GetString(rawBytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1.GetString(rawBytes), "ANSI");
        }
    }

    public static string ComputeSha256Hex(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string ComputeNormalizedHash(string text)
    {
        var sb = new StringBuilder(text.Length);
        using var sr = new StringReader(text);
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            sb.Append(line.TrimEnd()).Append('\n');
        }
        var trimmed = sb.ToString().TrimEnd();
        return ComputeSha256Hex(Encoding.UTF8.GetBytes(trimmed));
    }

    public static bool IsCppOnly(string content) =>
        CppMarkers.Any(m => content.Contains(m, StringComparison.Ordinal));

    public static List<string> MeaningfulLines(string content, SourceLanguage lang)
    {
        var src = lang == SourceLanguage.Python
            ? content
            : Regex.Replace(content, @"/\*[\s\S]*?\*/", string.Empty);

        var lines = new List<string>();
        using var sr = new StringReader(src);
        string? raw;
        while ((raw = sr.ReadLine()) is not null)
        {
            var line = raw.Trim();
            if (lang == SourceLanguage.Python)
            {
                if (line.StartsWith('#'))
                    continue;
            }
            else
            {
                var idx = line.IndexOf("//", StringComparison.Ordinal);
                if (idx >= 0)
                    line = line[..idx].Trim();
            }
            if (string.IsNullOrEmpty(line))
                continue;
            if (line.StartsWith("int main", StringComparison.Ordinal) || line.StartsWith("void main", StringComparison.Ordinal))
            {
                var brace = line.IndexOf('{');
                if (brace < 0)
                    continue;
                line = line[(brace + 1)..].Replace("return 0;", string.Empty).Trim(' ', '\t', '{', '}');
                if (string.IsNullOrEmpty(line))
                    continue;
            }
            else if (line.Length < 40 && BoilerplatePrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal)))
            {
                continue;
            }
            if (line.All(c => c is '{' or '}' or ';' or '(' or ')'))
                continue;

            var collapsed = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            lines.Add(collapsed);
        }
        return lines;
    }

    public static bool IsTrivialOrTemplate(string content, SourceLanguage lang) =>
        MeaningfulLines(content, lang).Count == 0;

    public static string StripDiacritics(string s)
    {
        var replaced = s.Replace('đ', 'd').Replace('Đ', 'D');
        var norm = replaced.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (var ch in norm)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string NormalizeTitle(string title)
    {
        var plain = StripDiacritics(title);
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in plain)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                words.Add(TitleWord(current.ToString()));
                current.Clear();
            }
        }
        if (current.Length > 0)
            words.Add(TitleWord(current.ToString()));

        var outSb = new StringBuilder();
        foreach (var w in words)
        {
            if (outSb.Length > 0 && outSb.Length + 1 + w.Length > 60)
                break;
            if (outSb.Length > 0)
                outSb.Append('_');
            outSb.Append(w);
        }
        return outSb.ToString();

        static string TitleWord(string w)
        {
            var lower = w.ToLowerInvariant();
            return char.ToUpperInvariant(lower[0]) + lower[1..];
        }
    }

    public static string SafeStem(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            var mapped = char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' ? c : '_';
            if (mapped == '_' && sb.Length > 0 && sb[^1] == '_')
                continue;
            sb.Append(mapped);
        }
        var trimmed = sb.ToString().Trim('_');
        if (string.IsNullOrEmpty(trimmed))
            return "untitled";
        return trimmed.Length > 100 ? trimmed[..100] : trimmed;
    }

    public static bool LooksLikePtitCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;
        int letters = code.TakeWhile(char.IsAsciiLetter).Count();
        var rest = code[letters..];
        return letters is >= 1 and <= 4 && rest.Length is >= 3 and <= 7 && rest.All(char.IsAsciiDigit);
    }

    public static string TargetExtension(ProblemContext? ctx, SourceLanguage sourceLang, string content)
    {
        if (sourceLang == SourceLanguage.Python)
            return "py";
        if (sourceLang == SourceLanguage.Header)
            return "h";

        if (ctx is not null && ctx.Platform == OjPlatform.Ptit)
        {
            var up = ctx.Code.ToUpperInvariant();
            if (up.StartsWith("CPP", StringComparison.Ordinal))
                return "cpp";
            if (up.StartsWith("PY", StringComparison.Ordinal))
                return "py";
            if (up.StartsWith('C') && up.Length > 1 && char.IsAsciiDigit(up[1]))
                return IsCppOnly(content) ? "cpp" : "c";
        }
        return DefaultExtension(sourceLang);
    }

    public static string GetCategoryFolder(ProblemContext? ctx)
    {
        if (ctx is null)
            return "Offline";
        if (ctx.Platform == OjPlatform.Ptit)
        {
            var letters = new string(ctx.Code.TakeWhile(char.IsAsciiLetter).ToArray()).ToUpperInvariant();
            return Path.Combine("PTIT", string.IsNullOrEmpty(letters) ? "Other" : letters);
        }
        return ctx.Platform.ToString();
    }

    public static string DescribeOffline(string src, SourceLanguage lang)
    {
        var compact = new string(src.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());
        var tags = new List<string>();
        void Push(string tag)
        {
            if (!tags.Contains(tag))
                tags.Add(tag);
        }
        bool HasAny(params string[] needles) => needles.Any(n => compact.Contains(n, StringComparison.Ordinal));

        if (compact.Contains("prime") || compact.Contains("nguyento") ||
            ((compact.Contains("%i==0") || compact.Contains("%j==0")) && (compact.Contains("sqrt(") || compact.Contains("i*i<="))))
            Push("Prime");
        if (HasAny("gcd(", "ucln", "lcm(", "bcnn")) Push("Gcd_Lcm");
        if (HasAny("fib", "fibonacci")) Push("Fibonacci");
        if (HasAny("factorial", "giaithua", "fact(")) Push("Factorial");
        if (HasAny("palin", "doixung")) Push("Palindrome");
        if (compact.Contains("mid=") && HasAny("l<=r", "left<=right", "low<=high")) Push("Binary_Search");
        if (HasAny("strlen(", "fgets(", "gets(", "strcmp(", "strcpy(", "strcat(", "strcspn(", "getline(", "std::string", "chars[", "chars"))
            Push("String");
        if (HasAny("toupper(", "tolower(", "-=32", "+=32", ".upper()", ".lower()", ".title()"))
            Push("Case_Convert");
        if (HasAny("reverse", "strrev(", "[::-1]")) Push("Reverse");
        if (HasAny("][j]", "[i][")) Push("Matrix");
        if (HasAny("a[j]<a[j-1]", "a[i]>a[i+1]", "a[i]<a[i-1]", "a[j]>a[j+1]")) Push("Sorted_Check");
        if (HasAny("qsort(", "sort(", "sorted(", ".sort(")) Push("Sort");
        if (compact.Contains("%10") && (compact.Contains("/=10") || compact.Contains("/10"))) Push("Digits");
        if (HasAny("struct", "typedef")) Push("Struct");
        if (HasAny("malloc(", "calloc(", "realloc(", "newint")) Push("Dynamic_Memory");
        if (HasAny("fopen(", "freopen(")) Push("File_IO");
        if (tags.Count == 0 && (compact.Contains("a[n]") || compact.Contains("[i]"))) Push("Array");
        if (tags.Count < 2 && (compact.Contains("sum") || compact.Contains("tong"))) Push("Sum");
        if (tags.Count < 2 && HasAny("max", "min")) Push("Max_Min");
        if (tags.Count < 2 && HasAny("cnt", "count", "dem")) Push("Count");
        if (tags.Count < 2 && compact.Contains("%2")) Push("Parity");
        if (tags.Count == 0 && (HasAny("%.1f", "%.2f", "%.3f", "%.4f", "%lf") || (lang == SourceLanguage.Python && compact.Contains(":.2f"))))
            Push("Float_Format");

        var parts = tags.Take(3).ToList();
        if (parts.Count == 0)
            parts.Add("Basic_IO");
        return string.Join('_', parts);
    }

    public static string BuildProblemBaseName(ProblemContext? ctx, string sourcePath, string text, SourceLanguage lang)
    {
        if (ctx is not null)
        {
            var normTitle = NormalizeTitle(ctx.Title);
            var raw = string.IsNullOrEmpty(normTitle) ? ctx.Code : $"{ctx.Code}_{normTitle}";
            return SafeStem(raw);
        }

        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var desc = DescribeOffline(text, lang);
        return SafeStem($"{desc}_{stem}");
    }

    public static string? SanitizeCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var sb = new StringBuilder();
        foreach (var c in raw.Trim())
        {
            var mapped = c is ' ' or '/' or '.' ? '_' : c;
            if (char.IsAsciiLetterOrDigit(mapped) || mapped is '_' or '-')
            {
                sb.Append(mapped);
                if (sb.Length >= 48)
                    break;
            }
        }
        var res = sb.ToString().Trim('_', '-');
        return string.IsNullOrEmpty(res) ? null : res;
    }

    public static string CleanTitle(string? raw, string code)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;
        var t = raw.Trim();
        if (!string.IsNullOrEmpty(code))
        {
            var shortCode = code.Contains('_') ? code.Split('_').Last() : code;
            foreach (var prefix in new[] { code, shortCode })
            {
                if (t.Length > prefix.Length && t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var rest = t[prefix.Length..].TrimStart();
                    if (rest.Length > 0 && rest[0] is '-' or ':' or '.' or '–' or '—')
                    {
                        t = rest[1..].Trim();
                        break;
                    }
                }
            }
        }
        t = Regex.Replace(t, @"^[A-Z]\d?\.\s+", string.Empty);
        return string.Join(' ', t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static ProblemContext? ParseContextFromUrl(string url, string? titleHint, long seenAtUnixMs)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            if (!Uri.TryCreate("https://" + url.Trim(), UriKind.Absolute, out uri))
                return null;
        }
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];
        var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        OjPlatform platform;
        string? code = null;

        if (IsDomainMatch(host, "code.ptit.edu.vn"))
        {
            platform = OjPlatform.Ptit;
            for (int i = 0; i + 1 < segs.Length; i++)
            {
                if (segs[i] is "question" or "problem" or "questions")
                {
                    code = SanitizeCode(segs[i + 1])?.ToUpperInvariant();
                    break;
                }
            }
        }
        else if (IsDomainMatch(host, "codeforces.com"))
        {
            platform = OjPlatform.Codeforces;
            if (segs.Length >= 5 && segs[0] == "problemset" && segs[1] == "acmsguru" && segs[2] == "problem")
                code = $"CF_SGU{SanitizeCode(segs[4])?.ToUpperInvariant()}";
            else if (segs.Length >= 4 && segs[0] == "problemset" && segs[1] == "problem")
                code = $"CF_{SanitizeCode(segs[2] + segs[3])?.ToUpperInvariant()}";
            else if (segs.Length >= 3 && segs[0] == "problemset" && segs[1] == "problem")
                code = $"CF_{SanitizeCode(segs[2])?.ToUpperInvariant()}";
            else
            {
                for (int i = 0; i + 3 < segs.Length; i++)
                {
                    if ((segs[i] == "contest" || segs[i] == "gym") && segs[i + 2] == "problem")
                    {
                        code = $"CF_{SanitizeCode(segs[i + 1] + segs[i + 3])?.ToUpperInvariant()}";
                        break;
                    }
                }
            }
        }
        else if (IsDomainMatch(host, "oj.vnoi.info") && segs.Length >= 2 && segs[0] == "problem")
        {
            platform = OjPlatform.Vnoj;
            code = $"VNOJ_{SanitizeCode(segs[1])}";
        }
        else if ((IsDomainMatch(host, "leetcode.com") || IsDomainMatch(host, "leetcode.cn"))
                 && segs.Length >= 2 && segs[0] == "problems")
        {
            platform = OjPlatform.LeetCode;
            code = $"LC_{SanitizeCode(segs[1])}";
        }
        else if (IsDomainMatch(host, "hackerrank.com"))
        {
            platform = OjPlatform.HackerRank;
            for (int i = 0; i + 1 < segs.Length; i++)
            {
                if (segs[i] == "challenges")
                {
                    code = $"HR_{SanitizeCode(segs[i + 1])}";
                    break;
                }
            }
        }
        else
        {
            return null;
        }

        if (string.IsNullOrEmpty(code))
            return null;

        return new ProblemContext(platform, code, CleanTitle(titleHint, code), url.Trim(), "Extension", seenAtUnixMs);
    }

    private static bool IsDomainMatch(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    public static string StripBrowserSuffix(string title)
    {
        var t = title.Replace("\u200b", string.Empty);
        while (true)
        {
            var idx = t.LastIndexOf(" - ", StringComparison.Ordinal);
            if (idx < 0)
                break;
            var tail = t[(idx + 3)..].ToLowerInvariant();
            bool isSuffix = tail.Contains("edge") || tail.Contains("thorium") || tail.Contains("chrom")
                || tail.Contains("brave") || tail.Contains("cốc cốc") || tail.Contains("coc coc")
                || tail.Contains("firefox") || tail.StartsWith("profile") || tail.StartsWith("personal")
                || tail.StartsWith("work") || tail.StartsWith("inprivate") || tail.StartsWith("hồ sơ")
                || tail.StartsWith("cá nhân") || tail is "code ptit" or "codeptit";
            if (isSuffix)
                t = t[..idx];
            else
                break;
        }

        t = Regex.Replace(t, @"\s+(?:and|và)\s+\d+\s+(?:more\s+pages?|trang\s+nữa)\s*$", string.Empty, RegexOptions.IgnoreCase);
        return t.Trim();
    }

    public static ProblemContext? ParseContextFromWindowTitle(string windowTitle, long seenAtUnixMs)
    {
        var t = StripBrowserSuffix(windowTitle);
        if (t.StartsWith('[') && t.IndexOf(']') is int close && close > 1)
        {
            var code = SanitizeCode(t[1..close]);
            if (code is not null)
            {
                var platform = GuessPlatformFromCode(code);
                var title = CleanTitle(t[(close + 1)..], code);
                return new ProblemContext(platform, code, title, string.Empty, "WindowTitle", seenAtUnixMs);
            }
        }

        var parts = t.Split(" - ");
        if (parts.Length >= 2 && parts[^1].Trim().Equals("codeforces", StringComparison.OrdinalIgnoreCase))
        {
            var head = parts[0].Trim();
            if (parts.Length >= 3 && head.Equals("problem", StringComparison.OrdinalIgnoreCase))
            {
                var code = SanitizeCode(parts[1])?.ToUpperInvariant();
                if (code is not null)
                    return new ProblemContext(OjPlatform.Codeforces, $"CF_{code}", string.Empty, string.Empty, "WindowTitle", seenAtUnixMs);
            }
            if (parts.Length >= 3 && head.Length <= 10 && head.All(char.IsAsciiLetterOrDigit))
            {
                var code = SanitizeCode(head)?.ToUpperInvariant();
                if (code is not null)
                {
                    var title = string.Join(" - ", parts[1..^1]);
                    return new ProblemContext(OjPlatform.Codeforces, $"CF_{code}", CleanTitle(title, code), string.Empty, "WindowTitle", seenAtUnixMs);
                }
            }
            var dotIdx = head.IndexOf(". ", StringComparison.Ordinal);
            if (dotIdx is > 0 and <= 2 && head[..dotIdx].All(char.IsAsciiLetterOrDigit))
            {
                var code = SanitizeCode(head[..dotIdx])?.ToUpperInvariant();
                if (code is not null)
                    return new ProblemContext(OjPlatform.Codeforces, $"CF_{code}", CleanTitle(head[(dotIdx + 2)..], code), string.Empty, "WindowTitle", seenAtUnixMs);
            }
        }

        var firstTok = t.Split(new[] { ' ', '-', ':', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstTok is not null && LooksLikePtitCode(firstTok))
        {
            var code = firstTok.ToUpperInvariant();
            return new ProblemContext(OjPlatform.Ptit, code, CleanTitle(t, code), string.Empty, "WindowTitle", seenAtUnixMs);
        }

        return null;
    }

    public static OjPlatform GuessPlatformFromCode(string code)
    {
        var up = code.ToUpperInvariant();
        if (up.StartsWith("CF_", StringComparison.Ordinal)) return OjPlatform.Codeforces;
        if (up.StartsWith("VNOJ_", StringComparison.Ordinal)) return OjPlatform.Vnoj;
        if (up.StartsWith("LC_", StringComparison.Ordinal)) return OjPlatform.LeetCode;
        if (up.StartsWith("HR_", StringComparison.Ordinal)) return OjPlatform.HackerRank;
        if (LooksLikePtitCode(up)) return OjPlatform.Ptit;
        return OjPlatform.Other;
    }
}

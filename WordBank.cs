using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TypingBot;

internal sealed record BankEntry(string Cat, string Word, string Input);

internal sealed class WordBank
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly string[] KnownCats =
    {
        "zh-elem", "zh-mid", "en-elem", "en-mid", "en-cet", "custom"
    };

    private static readonly Regex PairRegex = new(
        "\\[\\s*\"(?<w>(?:\\\\.|[^\"\\\\])*)\"\\s*,\\s*\"(?<p>(?:\\\\.|[^\"\\\\])*)\"\\s*\\]",
        RegexOptions.Compiled);

    private readonly List<BankEntry> _entries = new();
    private readonly string _cat;

    private WordBank(string cat) => _cat = cat;

    public int Count => _entries.Count;

    public static WordBank Load(string htmlPath, string cat, string? customJson)
    {
        var bank = new WordBank(string.IsNullOrWhiteSpace(cat) ? "zh-elem" : cat);
        var html = File.ReadAllText(htmlPath);
        bank.LoadFallback(html);
        bank.LoadBankDirectory(Path.GetDirectoryName(htmlPath) ?? ".");
        bank.LoadCustom(customJson);
        return bank;
    }

    /// <summary>
    /// 支持本地 HTML 和线上 URL。线上模式会同时拉取 banks/manifest.json
    /// 里的完整词库，蒙面 Boss 才能还原出 fallback 之外的词。
    /// </summary>
    public static async Task<WordBank> LoadAsync(
        string gameTarget, string cat, string? customJson, CancellationToken ct)
    {
        var bank = new WordBank(string.IsNullOrWhiteSpace(cat) ? "zh-elem" : cat);

        if (Options.TryParseHttpUrl(gameTarget, out var pageUri))
        {
            var html = await Http.GetStringAsync(pageUri, ct);
            bank.LoadFallback(html);
            await bank.LoadRemoteBanksAsync(pageUri, ct);
        }
        else
        {
            var html = await File.ReadAllTextAsync(gameTarget, ct);
            bank.LoadFallback(html);
            bank.LoadBankDirectory(Path.GetDirectoryName(gameTarget) ?? ".");
        }

        bank.LoadCustom(customJson);
        return bank;
    }

    /// <summary>
    /// 把敌人显示串还原成真正需要按下的字符序列。
    /// 支持 normal / reverse-view / reverse-both / masked / repeat。
    /// </summary>
    public string? Resolve(EnemyInfo enemy, string bossHead = "")
    {
        var py = (enemy.Py ?? "").Trim();
        var word = (enemy.Word ?? "").Trim();
        if (py.Length == 0 || word.Length == 0)
            return null;

        var reversedWord = Reverse(word);
        var reversedPy = Reverse(py);
        var isMasked = py.Contains('?');
        // 镜中人与魔镜显示出来都是「词反转 + 拼音反转」，外部唯一可靠区别是 Boss 名：
        // 镜中人输入原序，魔镜输入倒序。
        var isDarkMirror = bossHead.Contains("魔镜", StringComparison.Ordinal);

        // 当前词库优先，其次其它词库，最后是全量兜底。
        var candidates = _entries
            .Where(e => e.Word == word || e.Word == reversedWord)
            .OrderBy(e => e.Cat == _cat ? 0 : 1)
            .ToList();

        if (isMasked)
        {
            foreach (var entry in candidates)
            {
                if (MaskedMatches(entry.Input, py, enemy.Done))
                    return entry.Input;
            }
            return null;
        }

        foreach (var entry in candidates)
        {
            var input = entry.Input;
            if (input.Length == 0)
                continue;

            if (entry.Word == word)
            {
                if (input == py) return input;               // normal
                if (input == reversedPy) return input;       // reverse-view
                if (Reverse(input) == py) return py;         // reverse-both（显示即输入）
            }
            else // entry.Word == reversedWord：Boss 把显示词倒过来了
            {
                if (isDarkMirror)
                {
                    // 魔镜：en.pinyin 本身已反转，显示串就是输入串。
                    if (Reverse(input) == py) return py;
                    if (input == py) return py;
                    continue;
                }

                // 镜中人：显示反转，但输入仍按原顺序。
                if (input == reversedPy) return input;
                if (input == py) return input;
            }
        }

        // 没有词库映射：normal 与 reverse-both 的显示串就是输入串。
        return IsLetters(py) ? py : null;
    }

    private static bool MaskedMatches(string input, string shown, string done)
    {
        if (input.Length != shown.Length)
            return false;
        if (input.Length == 0 || shown.Length == 0)
            return false;
        if (input[0] != shown[0] || input[^1] != shown[^1])
            return false;

        for (var i = 0; i < shown.Length; i++)
        {
            if (shown[i] != '?' && shown[i] != input[i])
                return false;
        }

        if (!string.IsNullOrEmpty(done) && !input.StartsWith(done, StringComparison.Ordinal))
            return false;

        return true;
    }

    private void LoadFallback(string html)
    {
        var start = html.IndexOf("var BANK_FALLBACK", StringComparison.Ordinal);
        if (start < 0)
            start = 0;
        var end = html.IndexOf("\n};", start, StringComparison.Ordinal);
        if (end < 0)
            end = html.Length;
        var section = html[start..end];

        foreach (var cat in KnownCats)
        {
            if (cat == "custom")
                continue;

            var marker = "'" + cat + "': [";
            var catStart = section.IndexOf(marker, StringComparison.Ordinal);
            if (catStart < 0)
                continue;
            catStart += marker.Length;
            var catEnd = section.IndexOf("\n  ]", catStart, StringComparison.Ordinal);
            if (catEnd < 0)
                catEnd = section.Length;
            AddPairs(section[catStart..catEnd], cat);
        }
    }

    private async Task LoadRemoteBanksAsync(Uri pageUri, CancellationToken ct)
    {
        var files = new List<(string Cat, string File)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifestUri = new Uri(pageUri, "banks/manifest.json");

        try
        {
            var manifest = await Http.GetStringAsync(manifestUri, ct);
            using var doc = JsonDocument.Parse(manifest.TrimStart('\uFEFF'));
            if (doc.RootElement.TryGetProperty("banks", out var banks) &&
                banks.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in banks.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var i) ? i.GetString() : null;
                    var file = item.TryGetProperty("file", out var f) ? f.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(file))
                        continue;
                    if (seen.Add(id))
                        files.Add((id, file));
                }
            }
        }
        catch
        {
            // 清单不可用时回退到内置的 5 个词库文件名。
        }

        if (files.Count == 0)
        {
            foreach (var cat in KnownCats)
            {
                if (cat != "custom" && seen.Add(cat))
                    files.Add((cat, cat + ".json"));
            }
        }

        foreach (var (cat, file) in files)
        {
            try
            {
                var bankUri = new Uri(manifestUri, file);
                var json = await Http.GetStringAsync(bankUri, ct);
                using var doc = JsonDocument.Parse(json.TrimStart('\uFEFF'));
                WalkJson(doc.RootElement, cat);
            }
            catch
            {
                // 单个线上词库失败不影响 fallback 和其它词库。
            }
        }
    }

    private void LoadBankDirectory(string htmlDirectory)
    {
        var dir = Path.Combine(htmlDirectory, "banks");
        if (!Directory.Exists(dir))
            return;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var cat = Path.GetFileNameWithoutExtension(file);
                WalkJson(doc.RootElement, cat);
            }
            catch
            {
                // 单个词库损坏不影响其它词库。
            }
        }
    }

    private void LoadCustom(string? customJson)
    {
        if (string.IsNullOrWhiteSpace(customJson))
            return;

        try
        {
            using var doc = JsonDocument.Parse(customJson);
            WalkJson(doc.RootElement, "custom");
        }
        catch
        {
            // 自定义词库损坏时忽略。
        }
    }

    private void WalkJson(JsonElement element, string cat)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            var items = element.EnumerateArray().ToArray();
            if (items.Length >= 2 &&
                items[0].ValueKind == JsonValueKind.String &&
                items[1].ValueKind == JsonValueKind.String)
            {
                Add(items[0].GetString() ?? "", items[1].GetString() ?? "", cat);
                return;
            }

            foreach (var item in items)
                WalkJson(item, cat);
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                WalkJson(property.Value, cat);
        }
    }

    private void AddPairs(string body, string cat)
    {
        foreach (Match match in PairRegex.Matches(body))
        {
            Add(UnescapeJs(match.Groups["w"].Value),
                UnescapeJs(match.Groups["p"].Value),
                cat);
        }
    }

    private void Add(string word, string input, string cat)
    {
        word = word.Trim();
        input = input.Trim().ToLowerInvariant();
        if (word.Length == 0 || input.Length == 0 || !IsLetters(input))
            return;
        if (_entries.Any(e => e.Cat == cat && e.Word == word && e.Input == input))
            return;
        _entries.Add(new BankEntry(cat, word, input));
    }

    private static bool IsLetters(string s)
    {
        foreach (var c in s)
        {
            if (c < 'a' || c > 'z')
                return false;
        }
        return s.Length > 0;
    }

    private static string Reverse(string s)
    {
        var chars = s.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    private static string UnescapeJs(string s)
    {
        if (!s.Contains('\\'))
            return s;

        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length)
            {
                sb.Append(c);
                continue;
            }

            var next = s[++i];
            switch (next)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '\\': sb.Append('\\'); break;
                case '"': sb.Append('"'); break;
                case '\'': sb.Append('\''); break;
                case 'u' when i + 4 < s.Length:
                    var hex = s.Substring(i + 1, 4);
                    if (ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                            null, out var code))
                    {
                        sb.Append((char)code);
                        i += 4;
                    }
                    else
                    {
                        sb.Append("\\u");
                    }
                    break;
                default:
                    sb.Append(next);
                    break;
            }
        }
        return sb.ToString();
    }
}

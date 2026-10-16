using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Panely.Core.Localization;

/// <summary>
/// 한영 문구 조회(08-commands-and-copy.md).
/// 1) windows-strings.json(Windows 전용/대체 문구) → 2) localization.catalog.json 원본 → 3) 영어 sourceKey.
/// 원본 치환자(%@, %lld, %1$@, %2$lld, %%)는 위치 인덱스를 보존해 변환하고, 인자는 format string으로 해석하지 않는다.
/// </summary>
public static class Loc
{
    private static readonly Dictionary<string, Dictionary<string, string>> Catalog = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Dictionary<string, string>> Overrides = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static bool _loaded;
    private static string _language = "en";

    /// <summary>UI 언어가 바뀌면 발생한다. 창·메뉴·팝오버는 즉시 다시 그린다.</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>현재 UI 언어 코드: "en" 또는 "ko".</summary>
    public static string Language => _language;

    public static CultureInfo Culture => _language == "ko" ? CultureInfo.GetCultureInfo("ko-KR") : CultureInfo.GetCultureInfo("en-US");

    /// <summary>설정값(system/en/ko)에서 실제 UI 언어를 정한다. en/ko 외에는 en.</summary>
    public static string Resolve(Panely.Domain.Model.AppLanguage setting, CultureInfo? systemCulture = null) => setting switch
    {
        Panely.Domain.Model.AppLanguage.En => "en",
        Panely.Domain.Model.AppLanguage.Ko => "ko",
        _ => (systemCulture ?? CultureInfo.InstalledUICulture).TwoLetterISOLanguageName == "ko" ? "ko" : "en",
    };

    public static void SetLanguage(string language)
    {
        EnsureLoaded();
        var normalized = language == "ko" ? "ko" : "en";
        if (normalized == _language) return;
        _language = normalized;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>sourceKey(영어 원문)로 현재 언어 문구를 찾아 인자를 채운다.</summary>
    public static string T(string sourceKey, params object?[] args)
    {
        EnsureLoaded();
        var template = Lookup(sourceKey, _language);
        return args.Length == 0 && !template.Contains('%') ? template : Format(template, args, Culture);
    }

    /// <summary>특정 언어로 조회(내보내기 이름·테스트용).</summary>
    public static string TIn(string language, string sourceKey, params object?[] args)
    {
        EnsureLoaded();
        var template = Lookup(sourceKey, language);
        return Format(template, args, language == "ko" ? CultureInfo.GetCultureInfo("ko-KR") : CultureInfo.GetCultureInfo("en-US"));
    }

    public static bool Has(string sourceKey)
    {
        EnsureLoaded();
        return Overrides.ContainsKey(sourceKey) || Catalog.ContainsKey(sourceKey);
    }

    private static string Lookup(string key, string language)
    {
        if (Overrides.TryGetValue(key, out var o))
        {
            if (o.TryGetValue(language, out var v) && v.Length > 0) return v;
            if (o.TryGetValue("en", out var en) && en.Length > 0) return en;
        }
        if (Catalog.TryGetValue(key, out var c))
        {
            if (c.TryGetValue(language, out var v) && v.Length > 0) return v;
            if (c.TryGetValue("en", out var en) && en.Length > 0) return en;
        }
        return key;
    }

    /// <summary>
    /// 원본 printf 스타일 치환자를 채운다. 위치 지정(%2$lld)은 그 인덱스, 비위치는 순서대로.
    /// 인자 문자열 안의 %, {, }는 그대로 출력된다.
    /// </summary>
    public static string Format(string template, IReadOnlyList<object?> args, CultureInfo culture)
    {
        var sb = new StringBuilder(template.Length + 16);
        var sequential = 0;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c != '%' || i + 1 >= template.Length) { sb.Append(c); continue; }
            if (template[i + 1] == '%') { sb.Append('%'); i++; continue; }

            var j = i + 1;
            int? position = null;
            var digitsStart = j;
            while (j < template.Length && char.IsAsciiDigit(template[j])) j++;
            if (j > digitsStart && j < template.Length && template[j] == '$')
            {
                position = int.Parse(template.AsSpan(digitsStart, j - digitsStart), CultureInfo.InvariantCulture) - 1;
                j++;
            }
            else
            {
                j = digitsStart;
            }

            // 길이 수식어(l, ll, h, q, z)
            while (j < template.Length && template[j] is 'l' or 'h' or 'q' or 'z') j++;
            if (j >= template.Length) { sb.Append(template, i, template.Length - i); break; }
            var conv = template[j];
            if (conv is not ('@' or 'd' or 'i' or 'u' or 'f' or 'g' or 's' or 'x'))
            {
                sb.Append(c);
                continue;
            }

            var index = position ?? sequential++;
            var arg = index >= 0 && index < args.Count ? args[index] : null;
            sb.Append(arg switch
            {
                null => string.Empty,
                IFormattable f when conv is 'd' or 'i' or 'u' => f.ToString("0", culture),
                IFormattable f => f.ToString(null, culture),
                _ => arg.ToString(),
            });
            i = j;
        }
        return sb.ToString();
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            var asm = typeof(Loc).Assembly;
            LoadCatalog(asm);
            LoadOverrides(asm);
            _loaded = true;
        }
    }

    private static void LoadCatalog(Assembly asm)
    {
        using var stream = asm.GetManifestResourceStream("Panely.localization.catalog.json");
        if (stream is null) return;
        using var doc = JsonDocument.Parse(stream);
        foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            var key = entry.GetProperty("sourceKey").GetString() ?? string.Empty;
            if (key.Length == 0) continue;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (entry.TryGetProperty("localizations", out var locs) && locs.ValueKind == JsonValueKind.Object)
            {
                foreach (var lang in locs.EnumerateObject())
                {
                    if (lang.Value.TryGetProperty("stringUnit", out var unit) && unit.TryGetProperty("value", out var value))
                        map[lang.Name] = value.GetString() ?? string.Empty;
                }
            }
            if (!map.ContainsKey("en") && entry.TryGetProperty("fallbackEnglish", out var fb))
                map["en"] = fb.GetString() ?? key;
            Catalog[key] = map;
        }
    }

    /// <summary>
    /// Localization/Strings/*.json 을 모두 읽는다(영역별 파일: core, reader, translation …).
    /// 형식: { "English source key": { "en": "...", "ko": "..." } }
    /// </summary>
    private static void LoadOverrides(Assembly asm)
    {
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("Panely.strings.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var lang in prop.Value.EnumerateObject())
                    map[lang.Name] = lang.Value.GetString() ?? string.Empty;
                Overrides[prop.Name] = map;
            }
        }
    }

    /// <summary>테스트/진단: 등록된 모든 Windows 문구 key.</summary>
    public static IReadOnlyCollection<string> OverrideKeys
    {
        get { EnsureLoaded(); return Overrides.Keys; }
    }

    /// <summary>번역 언어명: 현재 UI locale로 표시.</summary>
    public static string LanguageName(string code) => code switch
    {
        "ja" => T("Japanese"),
        "zh-Hans" => T("Chinese (Simplified)"),
        "zh-Hant" => T("Chinese (Traditional)"),
        "ko" => T("Korean"),
        "en" => T("English"),
        _ => code,
    };

    /// <summary>특정 언어로 표시한 번역 언어명(내보내기 파일 이름 등).</summary>
    public static string LanguageNameIn(string uiLanguage, string code) => code switch
    {
        "ja" => TIn(uiLanguage, "Japanese"),
        "zh-Hans" => TIn(uiLanguage, "Chinese (Simplified)"),
        "zh-Hant" => TIn(uiLanguage, "Chinese (Traditional)"),
        "ko" => TIn(uiLanguage, "Korean"),
        "en" => TIn(uiLanguage, "English"),
        _ => code,
    };
}

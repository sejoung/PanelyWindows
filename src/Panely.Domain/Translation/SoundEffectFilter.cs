using System.Text;
using Panely.Domain.Model;

namespace Panely.Domain.Translation;

/// <summary>
/// 효과음 제외(06 풍선 그룹과 필터, 참조 규칙 v1). "짧으면 제외"하지 않는다.
/// 알파벳 성격의 문자≤6, han이 있으면 제외하지 않음. 일본어/한국어만 적용, en/zh는 적용하지 않는다.
/// 검수 예: スッ, パチパチ, 짝짝 제외 / はい, メラス, 비켜, 비켜! 유지.
/// </summary>
public static class SoundEffectFilter
{
    public const int RuleVersion = 1;
    private const int MaxLetters = 6;

    private const string SmallVowels = "ァィゥェォぁぃぅぇぉャュョゃゅょヮゎ";
    private const string SmallTsu = "ッっ";
    private const string PlosiveKana = "ドバガゴズダボブグゲデパポプピペビ";
    // 단음절 효과음(보수적 목록)
    private const string KoreanSingleSfx = "쾅쿵퍽탁휙쿡뻥펑빡팍툭퉁똑딱착짝쫙슥쓱훅휭끽덜컥쿨찰철팡탕땅쨍꽝쾅";

    public static bool IsSoundEffect(string text, string language)
    {
        if (language is not (TranslationLanguages.Japanese or TranslationLanguages.Korean)) return false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var c in text) if (Scripts.IsHan(c)) return false;

        var letters = new StringBuilder();
        foreach (var c in text)
        {
            if (Scripts.IsKana(c) || Scripts.IsHangul(c) || Scripts.IsAsciiLetter(c)) letters.Append(c);
        }
        var core = letters.ToString();
        if (core.Length == 0 || core.Length > MaxLetters) return false;

        return language == TranslationLanguages.Japanese ? IsJapaneseSfx(core) : IsKoreanSfx(text, core);
    }

    private static bool IsJapaneseSfx(string s)
    {
        // 촉음/작은 모음 종결: スッ, ドンッ, キャァ
        var last = s[^1];
        if (SmallTsu.Contains(last) && s.Length >= 2) return true;
        if (SmallVowels.Contains(last) && s.Length >= 2 && !IsSmallYouon(s)) return true;
        // 장음: ゴー, ドーン, バーン (한 음절 + 장음 + 선택적 ン)
        var noLong = s.TrimEnd('ン', 'ん');
        if (noLong.Length >= 2 && noLong.Length <= 4 && noLong[1..].All(c => c == 'ー')) return true;
        // 최소 2글자 단위 반복: パチパチ, ドキドキ
        if (IsRepetition(s, 2)) return true;
        // 짧은 비음 리듬: ドン, バン, ガン
        if (s.Length == 2 && (s[1] == 'ン' || s[1] == 'ん') && PlosiveKana.Contains(s[0])) return true;
        return false;
    }

    // キャ, シュ 처럼 요음으로 끝나는 짧은 대사 일부를 보호(예: "ニャ" 같은 경우는 여전히 짧은 의성어지만 보수적으로 유지)
    private static bool IsSmallYouon(string s) => s.Length == 2 && "ャュョゃゅょ".Contains(s[1]);

    private static bool IsKoreanSfx(string original, string s)
    {
        // 문장 내부 구두점이 있으면 반복 대사로 보고 남긴다: "비켜, 비켜!"
        var trimmed = original.Trim();
        for (var i = 0; i < trimmed.Length - 1; i++)
        {
            var c = trimmed[i];
            if (c is ',' or '，' or '、' or '.' or '!' or '?' or '…' or '~') return false;
        }
        // 단음절 효과음
        if (s.Length == 1) return KoreanSingleSfx.Contains(s[0]);
        // 반복: 짝짝, 쿵쿵, 두근두근
        if (IsRepetition(s, 1)) return true;
        // 끝 음절 중복: 으아아아, 꺄아아
        if (s.Length >= 3 && s[^1] == s[^2] && s[^2] == s[^3]) return true;
        // 모음 교대/자음 교대: 알록달록, 울긋불긋, 티격태격
        if (s.Length == 4 && s[1] == s[3] && s[0] != s[2] && s.All(Scripts.IsHangul)) return true;
        return false;
    }

    /// <summary>s가 minUnit 이상 길이의 단위를 2번 이상 반복한 문자열인가.</summary>
    private static bool IsRepetition(string s, int minUnit)
    {
        for (var unit = minUnit; unit <= s.Length / 2; unit++)
        {
            if (s.Length % unit != 0) continue;
            var ok = true;
            for (var i = unit; i < s.Length && ok; i++) ok = s[i] == s[i % unit];
            if (ok) return true;
        }
        return false;
    }
}

using TMPro;
using UnityEngine;

/// 한글 글리프 TMP 폰트 단일 소스(캐시). Assets/Resources/NotoSansKR.otf 우선,
/// 없으면 OS 한글 폰트(Malgun Gothic/Noto Sans CJK KR 등)로 런타임 생성.
public static class KoreanFont
{
    static TMP_FontAsset _font;
    static bool _tried;

    public static TMP_FontAsset Get()
    {
        if (_tried) return _font;
        _tried = true;
        Font f = Resources.Load<Font>("NotoSansKR");
        if (f == null)
        {
            string[] names = {
                "Malgun Gothic", "맑은 고딕", "NanumGothic", "나눔고딕",
                "Noto Sans CJK KR", "Noto Sans KR", "Apple SD Gothic Neo", "Gulim", "Dotum",
            };
            try { f = Font.CreateDynamicFontFromOSFont(names, 32); }
            catch (System.Exception ex) { Debug.LogWarning($"[KoreanFont] OS 폰트 로드 실패: {ex.Message}"); }
        }
        if (f == null)
        {
            Debug.LogWarning("[KoreanFont] 한글 폰트 없음 — Assets/Resources/NotoSansKR.otf 확인.");
            return null;
        }
        try { _font = TMP_FontAsset.CreateFontAsset(f); }
        catch (System.Exception ex) { Debug.LogWarning($"[KoreanFont] TMP 폰트 생성 실패: {ex.Message}"); }
        return _font;
    }
}

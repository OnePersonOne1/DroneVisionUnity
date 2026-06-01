using UnityEngine;

/// 검출 클래스 → 색/한글 라벨 단일 소스. (이전엔 ProjectionUdpReceiver/ProjectionReplay/
/// FirstPersonView 에 ColorFor 가 중복돼 있었음 → 여기로 통일.)
public static class DetectionClasses
{
    public struct Entry
    {
        public string name;   // 데이터 클래스명(영문)
        public string ko;     // 표시용 한글
        public Color color;
        public Entry(string n, string k, Color c) { name = n; ko = k; color = c; }
    }

    public static readonly Entry[] All =
    {
        new Entry("human",        "사람", Color.green),
        new Entry("fire_region",  "화재", Color.red),
        new Entry("smoke_region", "연기", Color.gray),
        new Entry("vehicle",      "차량", Color.cyan),
        new Entry("building",     "건물", Color.yellow),
        new Entry("lake",         "호수", Color.blue),
    };

    public static Color ColorFor(string cls)
    {
        for (int i = 0; i < All.Length; i++)
            if (All[i].name == cls) return All[i].color;
        return Color.magenta;   // 미정의 클래스
    }

    public static string KoreanFor(string cls)
    {
        for (int i = 0; i < All.Length; i++)
            if (All[i].name == cls) return All[i].ko;
        return cls;
    }
}

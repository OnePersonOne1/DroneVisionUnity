using System.Collections.Generic;
using UnityEngine;

/// 스폰된 검출 마커에 붙어 전역 레지스트리에 등록 — 미니맵/범주표가 활성 검출을 추적.
/// 마커가 수명(lifetime)으로 파괴되면 OnDisable 에서 자동 해제.
public class DetectionMarker : MonoBehaviour
{
    public static readonly List<DetectionMarker> All = new List<DetectionMarker>();

    public string className;
    public Color color;

    public void Init(string cls, Color c)
    {
        className = cls;
        color = c;
    }

    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }
}

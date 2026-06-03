using UnityEngine;

/// <summary>
/// 검출/모의 화재 마커의 클래스별 외관 빌더. FireSim 과 ProjectionUdpReceiver 가 공유.
///
///   fire_region  → 불투명 emissive **Cube** (직사각형, scale 10×10×10 기본)
///   smoke_region → 반투명 회색 **Cylinder** (기본 폭 4·높이 14)
///   그 외        → 기존 emissive Capsule (capsuleScale 기본 3)
///
/// 호출부: `var vis = DetectionMarkerVisual.BuildForClass(root.transform, className, color, settings);`
/// 부모(root) 는 피벗=바닥 위치. visual 의 localPosition.y 가 절반 만큼 위로 올라가 root 바닥 정렬.
/// </summary>
public static class DetectionMarkerVisual
{
    [System.Serializable]
    public struct Settings
    {
        [Tooltip("fire 마커 큐브 한 변 (Unity 단위).")]
        public float fireBoxSize;
        [Tooltip("smoke 마커 원기둥 반경.")]
        public float smokeRadius;
        [Tooltip("smoke 마커 원기둥 높이.")]
        public float smokeHeight;
        [Tooltip("그 외 클래스 capsule 폭(높이는 ×5).")]
        public float capsuleScale;
        [Tooltip("smoke 색 + 알파 (반투명).")]
        public Color smokeColor;

        public static Settings Default => new Settings
        {
            fireBoxSize = 10f,
            smokeRadius = 4f,
            smokeHeight = 14f,
            capsuleScale = 3f,
            smokeColor = new Color(0.5f, 0.5f, 0.5f, 0.45f),
        };
    }

    public static GameObject BuildForClass(Transform parent, string className, Color color)
        => BuildForClass(parent, className, color, Settings.Default);

    public static GameObject BuildForClass(Transform parent, string className, Color color, Settings s)
    {
        string cn = (className ?? "").ToLower();
        if (cn.Contains("fire"))  return MakeFireBox(parent, color, s.fireBoxSize);
        if (cn.Contains("smoke")) return MakeSmokeCylinder(parent, s.smokeColor, s.smokeRadius, s.smokeHeight);
        return MakeCapsule(parent, color, s.capsuleScale);
    }

    static GameObject MakeFireBox(Transform parent, Color color, float side)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "Visual_fire";
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.localScale = new Vector3(side, side, side);
        go.transform.localPosition = new Vector3(0f, side * 0.5f, 0f);   // 피벗 = 바닥.
        var col = go.GetComponent<Collider>(); if (col != null) Object.Destroy(col);
        TintEmissive(go.GetComponent<Renderer>(), color);
        return go;
    }

    static GameObject MakeSmokeCylinder(Transform parent, Color color, float radius, float height)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = "Visual_smoke";
        if (parent != null) go.transform.SetParent(parent, false);
        // Unity 기본 Cylinder primitive = 반경 0.5 · 높이 2. localScale.x/z = 반경×2, .y = 높이/2.
        go.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
        go.transform.localPosition = new Vector3(0f, height * 0.5f, 0f);
        var col = go.GetComponent<Collider>(); if (col != null) Object.Destroy(col);
        ApplyTranslucent(go.GetComponent<Renderer>(), color);
        return go;
    }

    static GameObject MakeCapsule(Transform parent, Color color, float scale)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = "Visual";
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.localScale = new Vector3(scale, scale * 5f, scale);
        go.transform.localPosition = new Vector3(0f, go.transform.localScale.y, 0f);
        var col = go.GetComponent<Collider>(); if (col != null) Object.Destroy(col);
        TintEmissive(go.GetComponent<Renderer>(), color);
        return go;
    }

    static void TintEmissive(Renderer r, Color c)
    {
        if (r == null) return;
        var mat = r.material;   // 인스턴스 사본.
        if (mat.HasProperty("_Color")) mat.color = c;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        if (mat.HasProperty("_EmissionColor"))
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", c * 2f);
        }
    }

    /// <summary>URP/Built-in 호환 반투명 머티리얼 변환.</summary>
    static void ApplyTranslucent(Renderer r, Color c)
    {
        if (r == null) return;
        var mat = r.material;
        if (mat.HasProperty("_Color")) mat.color = c;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        // URP Lit 의 _Surface 1 = Transparent.
        if (mat.HasProperty("_Surface"))
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_SURFACE_TYPE_OPAQUE");
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetInt("_ZWrite", 0);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        // emission 끔 — 연기는 발광체가 아니다.
        if (mat.HasProperty("_EmissionColor"))
        {
            mat.DisableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", Color.black);
        }
    }
}

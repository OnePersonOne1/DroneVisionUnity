using System.Collections.Generic;
using UnityEngine;

/// [Phase 3] 선택된 건물을 색상 반투명 **3D 박스**로 감싸 표시.
///
/// 맵이 단일 통합 메시라 건물 mesh 자체는 recolor 불가 → 백엔드가 준 OSM 풋프린트
/// (lat/lng)로 가로 AABB 를 만들고 마진만큼 확장, 높이는 GIS 지상층수×층고(없으면
/// GIS 높이)+마진으로 박스를 세운다. 색은 그 건물의 선택 색(ARInfoPanel 카드와 1:1).
/// 박스가 건물보다 마진만큼 커서 면이 공중에 떠 z-fighting 없이 보인다(CameraLift 불필요).
/// 지면 Y/수직 스케일은 CubeGPSDisplay(buildingBaseY, unityUnitsPerMeter)에서 가져온다.
[DisallowMultipleComponent]
public class BuildingFootprintHighlighter : MonoBehaviour
{
    [Header("Refs (비우면 자동 검색)")]
    public BuildingInfoService service;
    public BuildingInfoProbe probe;

    [Header("박스 여유 마진")]
    [Tooltip("풋프린트를 사방으로 이만큼(m) 확장.")]
    public float horizontalMarginMeters = 4f;
    [Tooltip("건물 높이에 이만큼(m) 더함.")]
    public float heightMarginMeters = 4f;
    [Tooltip("층수 → 높이 환산 층고(m).")]
    public float metersPerFloor = 3.3f;
    [Tooltip("층수·높이 정보가 없을 때 기본 박스 높이(m).")]
    public float defaultHeightMeters = 10f;
    [Tooltip("수직 스케일 보정 배수. 맵 세로 스케일이 uupm 으로 안 잡힐 때 보정. " +
             "CubeGPSDisplay.altitudeReferenceBuilding 가 제대로 연결돼 uupm 이 맞으면 1 로.")]
    public float heightScaleMultiplier = 4f;
    [Tooltip("박스 불투명도(건물이 비쳐 보이도록).")]
    [Range(0.05f, 1f)] public float boxAlpha = 0.5f;

    class Overlay { public GameObject go; public Mesh mesh; public Material mat; }
    readonly List<Overlay> _pool = new List<Overlay>();

    void Start()
    {
        if (service == null) service = FindObjectOfType<BuildingInfoService>();
        if (probe == null) probe = FindObjectOfType<BuildingInfoProbe>();
        if (service != null) service.SelectionsChanged += Rebuild;
        else Debug.LogWarning("[FootprintHL] BuildingInfoService 없음 — 박스 표시 불가.");
        Debug.Log($"[FootprintHL] 시작 (service={(service != null)}, probe={(probe != null)})");
    }

    void OnDestroy()
    {
        if (service != null) service.SelectionsChanged -= Rebuild;
        foreach (var ov in _pool)
            if (ov.go != null) Destroy(ov.go);
    }

    void Rebuild(List<BuildingInfoService.Selection> sels)
    {
        int i = 0;
        for (; i < sels.Count; i++)
        {
            var sel = sels[i];
            var ov = EnsureOverlay(i);
            float[] box = sel.info != null ? sel.info.box : null;
            if (probe == null || box == null || box.Length < 8)
            {
                ov.go.SetActive(false);
                continue;
            }
            BuildBox(ov, sel);
            Color c = new Color(sel.color.r, sel.color.g, sel.color.b, boxAlpha);
            var cols = new Color[ov.mesh.vertexCount];
            for (int k = 0; k < cols.Length; k++) cols[k] = c;
            ov.mesh.colors = cols;
            ov.mat.color = c;
            ov.go.SetActive(true);
        }
        for (int j = i; j < _pool.Count; j++) _pool[j].go.SetActive(false);
    }

    void BuildBox(Overlay ov, BuildingInfoService.Selection sel)
    {
        // OBB 4코너(lat,lng × 4, 건물 방향 정렬) → 월드 XZ. GpsToWorld 는 회전 미적용
        // (캘리브레이션이 world+X=East, world+Z=North 로 맞춘다는 규약 그대로).
        float[] bc = sel.info.box;
        var c = new Vector3[4];
        for (int k = 0; k < 4; k++)
            c[k] = probe.GpsToWorld(bc[k * 2], bc[k * 2 + 1], 0f);

        // 마진: 직사각형 자체 축(u,v)으로 외측 확장(건물 회전 유지).
        Vector3 u = c[1] - c[0]; u.y = 0f; if (u.sqrMagnitude > 1e-6f) u.Normalize();
        Vector3 v = c[3] - c[0]; v.y = 0f; if (v.sqrMagnitude > 1e-6f) v.Normalize();
        float m = horizontalMarginMeters;
        var b = new Vector3[4]
        {
            c[0] - u * m - v * m, c[1] + u * m - v * m,
            c[2] + u * m + v * m, c[3] - u * m + v * m,
        };

        // 높이: baseH × multiplier + 마진(m), uupm 으로 월드 환산.
        float uupm = UnityUnitsPerMeter();
        float y0 = GroundY(sel.hitY);
        float baseH = Mathf.Max(sel.info.gis_height_m, sel.info.gis_floors * metersPerFloor);
        if (baseH <= 0f) baseH = defaultHeightMeters;
        float effectiveMeters = baseH * heightScaleMultiplier + heightMarginMeters;
        float y1 = y0 + effectiveMeters * uupm;
        Debug.Log($"[FootprintHL] OBB baseH={baseH}m mult={heightScaleMultiplier} +margin={heightMarginMeters} eff={effectiveMeters}m uupm={uupm}");

        var verts = new Vector3[8];
        for (int k = 0; k < 4; k++)
        {
            verts[k] = new Vector3(b[k].x, y0, b[k].z);
            verts[k + 4] = new Vector3(b[k].x, y1, b[k].z);
        }

        var tris = new int[]
        {
            0,2,1, 0,3,2,    // 바닥
            4,5,6, 4,6,7,    // 윗면
            0,1,5, 0,5,4,    // 측면 0-1
            1,2,6, 1,6,5,    // 측면 1-2
            2,3,7, 2,7,6,    // 측면 2-3
            3,0,4, 3,4,7,    // 측면 3-0
        };
        ov.mesh.Clear();
        ov.mesh.vertices = verts;
        ov.mesh.uv = new Vector2[8];
        ov.mesh.triangles = tris;
        ov.mesh.RecalculateBounds();
    }

    /// 기준 건물 메쉬 높이 / 실제높이(m) = 월드단위/m (수직 스케일). CubeGPSDisplay 와 동일 산식.
    float UnityUnitsPerMeter()
    {
        var cal = probe.calibration;
        if (cal != null && cal.altitudeReferenceBuilding != null)
        {
            var r = cal.altitudeReferenceBuilding.GetComponent<Renderer>();
            float refM = (float)cal.referenceBuildingHeightMeters;
            if (r != null && r.bounds.size.y > 0.0001f && refM > 0.0001f)
                return r.bounds.size.y / refM;
        }
        return 1f;
    }

    float GroundY(float fallback)
    {
        var cal = probe.calibration;
        if (cal != null && cal.altitudeReferenceBuilding != null)
        {
            var r = cal.altitudeReferenceBuilding.GetComponent<Renderer>();
            if (r != null) return r.bounds.min.y;
        }
        return fallback;
    }

    Overlay EnsureOverlay(int i)
    {
        while (i >= _pool.Count)
        {
            // 부모에 붙이지 않음 — 메시 정점이 월드 좌표.
            var go = new GameObject($"BuildingBox_{_pool.Count}");
            go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            var mesh = new Mesh { name = "BuildingBox" };
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            mr.sharedMaterial = new Material(sh);
            go.SetActive(false);
            _pool.Add(new Overlay { go = go, mesh = mesh, mat = mr.sharedMaterial });
        }
        return _pool[i];
    }
}

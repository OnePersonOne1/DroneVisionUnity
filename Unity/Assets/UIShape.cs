using UnityEngine;
using UnityEngine.UI;

/// 임의의 2D 삼각형 메시를 그리는 UI Graphic. 미니맵의 드론 위치 원/시야(FOV) 폴리곤용.
/// 정점은 이 RectTransform 로컬 좌표(px). 색은 Graphic.color.
[RequireComponent(typeof(CanvasRenderer))]
public class UIShape : Graphic
{
    Vector2[] _verts;
    int[] _tris;

    public void SetMesh(Vector2[] verts, int[] tris)
    {
        _verts = verts;
        _tris = tris;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (_verts == null || _tris == null) return;
        var uv = new Vector4(0.5f, 0.5f, 0f, 0f);
        for (int i = 0; i < _verts.Length; i++)
            vh.AddVert(_verts[i], color, uv);
        for (int i = 0; i + 2 < _tris.Length; i += 3)
            vh.AddTriangle(_tris[i], _tris[i + 1], _tris[i + 2]);
    }
}

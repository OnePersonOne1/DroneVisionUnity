using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using DroneSim.Flight.UnityAdapter;

namespace DroneSim.C2.StrategicView
{
    /// <summary>
    /// 전략 보기 부트스트랩 — Display 3 (인덱스 strategicDisplayIndex) 에 **항상 전체 화면** 렌더.
    /// PiP/Expanded 토글 폐기 — 멀티 디스플레이 분담 (Display 1=FP, 2=TP, 3=Strategic) 가정.
    ///
    /// 구성:
    ///   - Strategic Camera: targetTexture=null, targetDisplay=2. 탑다운 ortho.
    ///   - Strategic Canvas: ScreenSpaceOverlay, targetDisplay=2. 마커 오버레이용.
    ///   - <see cref="MultiDisplayCoordinator"/> 가 Display.Activate 책임.
    ///
    /// 입력 라우팅(<see cref="TryGetStrategicViewport"/>):
    ///   Display.RelativeMouseAt 로 현재 마우스가 Display 3 위에 있는지 검사.
    ///   (Editor 단일 모니터 환경에선 동작 제한적 — build 권장.)
    /// </summary>
    public class StrategicViewBootstrap : MonoBehaviour
    {
        [Header("디스플레이")]
        [Tooltip("0-indexed. UI의 'Display 3' = 2. MultiDisplayCoordinator 와 같아야 함.")]
        public int strategicDisplayIndex = 2;

        [Header("Strategic Camera 초기값 (real meter, unityUnitsPerMeter 자동 곱)")]
        public float initialCameraHeightMeters = 200f;
        public float initialOrthoSizeMeters = 500f;
        public float farClipUnity = 1e8f;
        public float nearClipUnity = 0.1f;
        public Color backgroundColor = new Color(0.05f, 0.07f, 0.10f, 1f);
        public LayerMask renderLayers = ~0;


        Camera _cam;
        Canvas _canvas;
        RectTransform _canvasRt;
        float _scale = 1f;

        public Camera StrategicCamera => _cam;
        public Canvas Canvas => _canvas;
        public RectTransform CanvasRect => _canvasRt;
        public int DisplayIndex => strategicDisplayIndex;

        System.Collections.IEnumerator Start()
        {
            yield return null;
            _scale = FrameConversion.ResolveUnityUnitsPerMeter();
            if (_scale <= 0f) _scale = 1f;

            BuildCamera();
            BuildCanvas();
            Debug.Log($"[StrategicView] Display {strategicDisplayIndex} 전체화면 모드 scale={_scale}.");
        }

        void BuildCamera()
        {
            var go = new GameObject("Strategic Camera");
            go.transform.SetParent(transform, false);
            _cam = go.AddComponent<Camera>();
            _cam.orthographic = true;
            _cam.orthographicSize = initialOrthoSizeMeters * _scale;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = backgroundColor;
            _cam.cullingMask = renderLayers;
            _cam.nearClipPlane = nearClipUnity;
            _cam.farClipPlane = farClipUnity;
            _cam.targetTexture = null;                 // RT 미사용 — 디스플레이로 직접
            _cam.targetDisplay = strategicDisplayIndex;
            _cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            var anchor = GameObject.Find("Cube");
            Vector3 center = anchor != null ? anchor.transform.position : Vector3.zero;
            float h = initialCameraHeightMeters * _scale;
            _cam.transform.position = new Vector3(center.x, center.y + h, center.z);
        }

        void BuildCanvas()
        {
            var canvasGo = new GameObject("Strategic Canvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.targetDisplay = strategicDisplayIndex;
            _canvas.sortingOrder = 80;
            // Constant pixel — 마커 위치는 카메라 ScreenPoint(실 픽셀) 그대로 매핑.
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            canvasGo.AddComponent<GraphicRaycaster>();
            _canvasRt = canvasGo.GetComponent<RectTransform>();
        }

        /// <summary>마우스 화면 픽셀 → Strategic Camera viewport(0..1).
        /// 빌드 멀티 디스플레이: Display.RelativeMouseAt 로 정확 라우팅.
        /// Editor 단일 모니터: Strategic Camera 의 pixelWidth/Height 로 정규화 (Game view Display 3 탭 가정).</summary>
        public bool TryGetStrategicViewport(Vector2 screen, out Vector2 viewport)
        {
            viewport = default;
            if (_cam == null) return false;

            // 우선순위 1: Editor → 현재 마우스가 떠 있는 Game view 의 targetDisplay 가
            //   strategicDisplayIndex 인지 reflection 으로 검사. 다른 탭(Display 1, 2) 위 클릭은 무시.
            if (Application.isEditor)
            {
                if (!IsMouseOverStrategicGameView()) return false;
                float w = _cam.pixelWidth > 0 ? _cam.pixelWidth : Screen.width;
                float h = _cam.pixelHeight > 0 ? _cam.pixelHeight : Screen.height;
                if (w <= 0f || h <= 0f) return false;
                viewport = new Vector2(screen.x / w, screen.y / h);
            }
            // 우선순위 2: 빌드 멀티 디스플레이 — RelativeMouseAt 으로 정확 라우팅.
            else if (Display.displays.Length > strategicDisplayIndex)
            {
                Vector3 rel = Display.RelativeMouseAt(new Vector3(screen.x, screen.y, 0f));
                int idx = (int)rel.z;
                if (idx != strategicDisplayIndex) return false;
                Display d = Display.displays[strategicDisplayIndex];
                int w = d.systemWidth > 0 ? d.systemWidth : _cam.pixelWidth;
                int h = d.systemHeight > 0 ? d.systemHeight : _cam.pixelHeight;
                viewport = new Vector2(rel.x / w, rel.y / h);
            }
            else
            {
                return false;
            }
            if (viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f) return false;
            return true;
        }

        /// <summary>Strategic Camera 를 기준점(Cube) 위로 재중심. orthoSize 는 유지(현재 줌 보존).</summary>
        public void RecenterCamera()
        {
            if (_cam == null) return;
            var anchor = GameObject.Find("Cube");
            Vector3 center = anchor != null ? anchor.transform.position : Vector3.zero;
            float h = initialCameraHeightMeters * _scale;
            _cam.transform.position = new Vector3(center.x, center.y + h, center.z);
        }

#if UNITY_EDITOR
        /// <summary>Editor 한정: 현재 마우스가 호버 중인 Game view 의 targetDisplay 가
        /// strategicDisplayIndex 와 일치하는지 reflection 으로 검사. 멀티 Game view 탭 환경 대응.</summary>
        bool IsMouseOverStrategicGameView()
        {
            var w = UnityEditor.EditorWindow.mouseOverWindow;
            if (w == null) return false;
            var t = w.GetType();
            if (t.Name != "GameView") return false;
            const System.Reflection.BindingFlags F =
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;
            // 최신 Unity 는 property "targetDisplay", 옛 버전은 field "m_TargetDisplay".
            var prop = t.GetProperty("targetDisplay", F);
            if (prop != null && prop.PropertyType == typeof(int))
                return (int)prop.GetValue(w) == strategicDisplayIndex;
            var field = t.GetField("m_TargetDisplay", F);
            if (field != null && field.FieldType == typeof(int))
                return (int)field.GetValue(w) == strategicDisplayIndex;
            return false;
        }
#else
        bool IsMouseOverStrategicGameView() => true;
#endif
    }
}

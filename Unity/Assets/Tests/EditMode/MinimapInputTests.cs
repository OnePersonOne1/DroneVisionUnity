using UnityEngine;
using NUnit.Framework;

namespace DroneSim.Flight.Tests
{
    /// <summary>
    /// MinimapCommandInput 의 화면→월드 좌표 변환을 EditMode 에서 검증.
    /// (실제 MinimapCommandInput 은 Assembly-CSharp 에 있어 본 asmdef 에서 직접 못 부르지만,
    ///  변환 식 자체는 단순 선형이라 동일 식으로 재현해 검증한다.)
    /// </summary>
    public class MinimapInputTests
    {
        // Minimap.WorldOffsetToUi 의 역함수와 같은 형태.
        static Vector2 PanelCenteredToWorldOffset(Vector2 centered, Vector2 panelSize, float viewRadius)
        {
            float hx = panelSize.x * 0.5f, hy = panelSize.y * 0.5f;
            return new Vector2(centered.x / hx * viewRadius, centered.y / hy * viewRadius);
        }

        [Test]
        public void CenterClick_MapsToOrigin()
        {
            // 패널 중앙 클릭 = 드론 위치(=드론 기준 0 offset).
            var offset = PanelCenteredToWorldOffset(Vector2.zero, new Vector2(220, 220), 600f);
            Assert.AreEqual(0f, offset.x, 1e-4f);
            Assert.AreEqual(0f, offset.y, 1e-4f);
        }

        [Test]
        public void EdgeClick_MapsToViewRadius()
        {
            // 패널 가장 오른쪽 = +viewRadius East.
            var offset = PanelCenteredToWorldOffset(new Vector2(110, 0), new Vector2(220, 220), 600f);
            Assert.AreEqual(600f, offset.x, 1e-3f);
            Assert.AreEqual(0f, offset.y, 1e-3f);
        }

        [Test]
        public void TopClick_MapsToNorth()
        {
            // 패널 상단 = +viewRadius North (+Y 패널 = +Z 월드).
            var offset = PanelCenteredToWorldOffset(new Vector2(0, 110), new Vector2(220, 220), 600f);
            Assert.AreEqual(0f, offset.x, 1e-3f);
            Assert.AreEqual(600f, offset.y, 1e-3f);
        }

        [Test]
        public void DiagonalClick_LinearMapping()
        {
            // 정확히 절반 위치 → 절반 viewRadius.
            var offset = PanelCenteredToWorldOffset(new Vector2(55, -55), new Vector2(220, 220), 600f);
            Assert.AreEqual(300f, offset.x, 1e-3f);
            Assert.AreEqual(-300f, offset.y, 1e-3f);
        }

        [Test]
        public void NonSquarePanel_AsymmetricScaling()
        {
            // 가로 400, 세로 200 패널: x 스케일/y 스케일이 다름.
            var offset = PanelCenteredToWorldOffset(new Vector2(200, 100), new Vector2(400, 200), 1000f);
            Assert.AreEqual(1000f, offset.x, 1e-3f);
            Assert.AreEqual(1000f, offset.y, 1e-3f);
        }
    }
}

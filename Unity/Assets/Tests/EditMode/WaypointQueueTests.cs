using System.Numerics;
using DroneSim.Flight.Core;
using DroneSim.Flight.Core.Controllers;
using NUnit.Framework;

namespace DroneSim.Flight.Tests
{
    public class WaypointQueueTests
    {
        [Test]
        public void Empty_TargetOrHold_ReturnsDronePos()
        {
            var q = new WaypointQueue();
            var p = new Vector3(1, 2, 3);
            Assert.AreEqual(p, q.TargetOrHold(p));
            Assert.AreEqual(0, q.Pending);
        }

        [Test]
        public void SetSingle_ClearsAndSetsCurrent()
        {
            var q = new WaypointQueue();
            q.Append(new Vector3(1, 0, 0));
            q.Append(new Vector3(2, 0, 0));
            q.SetSingle(new Vector3(9, 9, 9));
            Assert.AreEqual(1, q.Pending);
            Assert.AreEqual(new Vector3(9, 9, 9), q.Current);
        }

        [Test]
        public void Append_FillsCurrentFirstThenQueue()
        {
            var q = new WaypointQueue();
            q.Append(new Vector3(1, 0, 0));
            Assert.AreEqual(new Vector3(1, 0, 0), q.Current);
            Assert.AreEqual(1, q.Pending);
            q.Append(new Vector3(2, 0, 0));
            Assert.AreEqual(new Vector3(1, 0, 0), q.Current); // 그대로
            Assert.AreEqual(2, q.Pending);
        }

        [Test]
        public void SetPath_LoadsInOrder()
        {
            var q = new WaypointQueue();
            q.SetPath(new[] { new Vector3(1,0,0), new Vector3(2,0,0), new Vector3(3,0,0) });
            Assert.AreEqual(3, q.Pending);
            Assert.AreEqual(new Vector3(1,0,0), q.Current);
        }

        [Test]
        public void Tick_AdvancesWhenArrivalSatisfied()
        {
            var q = new WaypointQueue { ArrivalRadius = 0.5f, ArrivalVelocity = 0.3f };
            q.SetPath(new[] { new Vector3(0,0,5), new Vector3(0,0,10) });
            // 첫 wp 에 도착.
            q.Tick(new Vector3(0,0,5.0f), Vector3.Zero);
            Assert.AreEqual(new Vector3(0,0,10), q.Current);
        }

        [Test]
        public void Tick_DoesNotAdvanceWhenStillMoving()
        {
            var q = new WaypointQueue { ArrivalRadius = 0.5f, ArrivalVelocity = 0.3f };
            q.SetPath(new[] { new Vector3(0,0,5), new Vector3(0,0,10) });
            // 위치는 도달했지만 빠르게 지나가는 중 → 큐 진행 안 함.
            q.Tick(new Vector3(0,0,5.0f), new Vector3(0,0,5f));
            Assert.AreEqual(new Vector3(0,0,5), q.Current);
        }

        [Test]
        public void Tick_HoldsAtLastWaypointWhenQueueEmpty()
        {
            var q = new WaypointQueue();
            q.SetSingle(new Vector3(0,0,5));
            q.Tick(new Vector3(0,0,5), Vector3.Zero); // 도달, 큐 비어 있음
            Assert.AreEqual(new Vector3(0,0,5), q.Current); // 그대로 유지(hold)
        }

        [Test]
        public void StopAndHold_ClearsAndSetsHold()
        {
            var q = new WaypointQueue();
            q.SetPath(new[] { new Vector3(1,0,0), new Vector3(2,0,0) });
            q.StopAndHold(new Vector3(0.5f, 0, 0));
            Assert.AreEqual(1, q.Pending);
            Assert.AreEqual(new Vector3(0.5f, 0, 0), q.Current);
        }

        // ── 통합: position → attitude → rate cascade 가 wp 로 수렴하는지 ───

        [Test]
        public void Integration_DroneReachesWaypoint_Within10Seconds()
        {
            var p = new DroneParams();
            var s = State6Dof.AtRest(Vector3.Zero);
            var pos = new PositionController();
            var att = new AttitudeController();
            var rate = new RatePid();
            var queue = new WaypointQueue { ArrivalRadius = 0.5f, ArrivalVelocity = 0.3f };
            queue.SetSingle(new Vector3(0, 0, 5));   // 5 m 상승

            float dt = 0.005f;          // 200Hz for test speed
            float t = 0f;
            while (t < 10f)
            {
                queue.Tick(s.P, s.V);
                Vector3 target = queue.TargetOrHold(s.P);
                pos.Step(s.P, s.V, target, p, dt, out float T, out Quaternion qDes);
                Vector3 omegaDes = att.Step(qDes, s.Q);
                Vector3 tau = rate.Step(omegaDes, s.Omega, dt);
                s = Rk4Integrator.Step(s, T, tau, p, dt);
                t += dt;
                float d = (s.P - new Vector3(0, 0, 5)).Length();
                if (d < 0.5f && s.V.Length() < 0.5f) return; // pass
            }
            float finalErr = (s.P - new Vector3(0, 0, 5)).Length();
            Assert.Fail($"10초 내 도달 실패. 최종 거리 {finalErr:F2} m, v={s.V.Length():F2} m/s");
        }
    }
}

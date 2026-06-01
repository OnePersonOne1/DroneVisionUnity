using System.Collections.Generic;
using System.Numerics;

namespace DroneSim.Flight.Core
{
    /// <summary>
    /// FIFO waypoint 큐 + 도달 판정. UnityEngine 무의존.
    ///
    /// 상태:
    ///   - <see cref="Current"/>: 현재 추종 목표 (null 이면 hover 모드 = 아래 TargetOrHold 가 입력 위치 반환).
    ///   - 내부 큐: 다음 waypoint 들 FIFO.
    ///
    /// 도달 판정 (<see cref="Tick"/>):
    ///   ‖p − Current‖ &lt; ArrivalRadius  AND  ‖v‖ &lt; ArrivalVelocity → 다음 wp 로.
    ///   큐 비면 Current 유지(드론은 wp 위에서 hover).
    ///
    /// 좌표는 모두 ENU (inertial, m).
    /// </summary>
    public sealed class WaypointQueue
    {
        public float ArrivalRadius = 5f;          // m — sphere of acceptance (PX4 NAV_ACC_RAD 대응)
        public float ArrivalVelocity = 1e9f;      // m/s — velocity 게이트 기본 off (loiter 시만 켜기)

        readonly Queue<Vector3> _queue = new Queue<Vector3>();
        public Vector3? Current { get; private set; }
        Vector3? _previousWp;   // ArduPilot/PX4 bearing test 용 — 직전 wp.
        float _prevDistSq = float.MaxValue;

        /// <summary>큐 + 현재 목표 합 (잔여 routing 길이).</summary>
        public int Pending => _queue.Count + (Current.HasValue ? 1 : 0);

        public IReadOnlyCollection<Vector3> Upcoming => _queue;

        /// <summary>큐 비우고 단일 목표 지정.</summary>
        public void SetSingle(in Vector3 w)
        {
            _queue.Clear();
            Current = w;
            _previousWp = null;
            _prevDistSq = float.MaxValue;
        }

        /// <summary>현재 목표 뒤에 추가. 현재 목표 없으면 새 단일 목표가 됨.</summary>
        public void Append(in Vector3 w)
        {
            if (!Current.HasValue)
            {
                Current = w; _previousWp = null; _prevDistSq = float.MaxValue;
            }
            else _queue.Enqueue(w);
        }

        /// <summary>경로 일괄 지정(기존 큐 폐기).</summary>
        public void SetPath(IList<Vector3> path)
        {
            _queue.Clear();
            Current = null;
            _previousWp = null;
            _prevDistSq = float.MaxValue;
            if (path == null) return;
            for (int i = 0; i < path.Count; i++) Append(path[i]);
        }

        /// <summary>큐 비우고 현재 위치에서 hover.</summary>
        public void StopAndHold(in Vector3 holdPos)
        {
            _queue.Clear();
            Current = holdPos;
            _previousWp = null;
            _prevDistSq = float.MaxValue;
        }

        /// <summary>전체 클리어(드론은 다음 tick 부터 외부 입력 없으면 무제어 — 호출 측 책임).</summary>
        public void Clear()
        {
            _queue.Clear();
            Current = null;
            _previousWp = null;
            _prevDistSq = float.MaxValue;
        }

        /// <summary>도달 판정 + 다음 wp 진행. 세 가지 중 하나라도 만족 시 advance:
        ///   (a) **Sphere of acceptance**: ‖p − wp‖ &lt; ArrivalRadius (PX4 NAV_ACC_RAD 방식).
        ///   (b) **Bearing test (PX4/ArduPilot)**: 직전 wp 가 있을 때 leg = wp − prev,
        ///       toWp = wp − p. dot(leg, toWp) ≤ 0 면 wp 의 perpendicular plane 을 넘은 것 = 통과.
        ///       오버슈트 / 사선 진입에도 정확히 잡힘.
        ///   (c) **Pass-through fallback**: bearing test 불가(첫 wp, prev 없음)일 때 직전 거리보다
        ///       멀어졌고 ArrivalRadius·3 안이었으면 통과 처리.</summary>
        public void Tick(in Vector3 pCurrent, in Vector3 vCurrent)
        {
            if (!Current.HasValue) return;
            Vector3 wp = Current.Value;
            float distSq = (pCurrent - wp).LengthSquared();
            float vSq = vCurrent.LengthSquared();
            float r2 = ArrivalRadius * ArrivalRadius;

            // (a) Sphere of acceptance (+ optional velocity gate).
            bool arrived = distSq < r2 && vSq < ArrivalVelocity * ArrivalVelocity;

            // (b) Bearing test — 이전 wp 가 있을 때.
            bool passedLeg = false;
            if (_previousWp.HasValue)
            {
                Vector3 leg = wp - _previousWp.Value;
                if (leg.LengthSquared() > 1e-6f)
                {
                    Vector3 toWp = wp - pCurrent;
                    if (Vector3.Dot(leg, toWp) <= 0f) passedLeg = true;
                }
            }

            // (c) Pass-through fallback.
            float r3sq = (ArrivalRadius * 3f) * (ArrivalRadius * 3f);
            bool passedThrough = !_previousWp.HasValue && distSq > _prevDistSq && _prevDistSq < r3sq;

            if (arrived || passedLeg || passedThrough)
            {
                if (_queue.Count > 0)
                {
                    _previousWp = wp;
                    Current = _queue.Dequeue();
                    _prevDistSq = float.MaxValue;
                    return;
                }
                // 큐 비면 Current 그대로 → 마지막 wp 에서 hover.
            }
            _prevDistSq = distSq;
        }

        /// <summary>제어기 입력용 타겟. 현재 목표 없으면 dronePos 반환(= 그 자리 hover).</summary>
        public Vector3 TargetOrHold(in Vector3 dronePos)
        {
            return Current ?? dronePos;
        }
    }
}

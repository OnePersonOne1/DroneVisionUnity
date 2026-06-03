using System;
using UnityEngine;

namespace DroneSim.C2
{
    /// <summary>
    /// KST 가상 시계. /assess 가 sim_time_iso 로 동봉.
    /// 기본은 실제 KST 현재. override 켜면 임의 시:분 으로 고정(날짜는 오늘 KST 유지).
    ///
    /// 자체 UI 없음 — SimClockUI 가 프리셋 버튼·시:분 입력 등을 그린다.
    /// 다른 컴포넌트(SituationAssessClient) 가 OnTimeChanged 구독.
    /// </summary>
    [DisallowMultipleComponent]
    public class SimClock : MonoBehaviour
    {
        [Header("Override 상태 (Play 중 변경 가능)")]
        public bool useOverride = false;
        [Range(0, 23)] public int overrideHour = 14;
        [Range(0, 59)] public int overrideMinute = 0;

        public event Action OnTimeChanged;

        // 마지막 emit 시점 비교 — 동일값 재발화 방지.
        bool _lastOverride;
        int _lastH, _lastM;
        bool _initialized;

        public static readonly TimeSpan KstOffset = TimeSpan.FromHours(9);

        /// <summary>지금의 KST DateTime. override 면 오늘 KST 날짜 + 지정 시:분.</summary>
        public DateTime NowKst()
        {
            DateTime nowKst = DateTime.UtcNow + KstOffset;
            if (!useOverride) return nowKst;
            return new DateTime(nowKst.Year, nowKst.Month, nowKst.Day,
                                Mathf.Clamp(overrideHour, 0, 23),
                                Mathf.Clamp(overrideMinute, 0, 59),
                                0, DateTimeKind.Unspecified);
        }

        /// <summary>ISO 8601 KST. 예: "2026-06-03T14:00:00+09:00".</summary>
        public string NowIsoKst()
        {
            var dt = NowKst();
            return dt.ToString("yyyy-MM-ddTHH:mm:ss") + "+09:00";
        }

        public void SetOverride(int h, int m)
        {
            useOverride = true;
            overrideHour = Mathf.Clamp(h, 0, 23);
            overrideMinute = Mathf.Clamp(m, 0, 59);
            EmitChange();
        }

        public void ClearOverride()
        {
            useOverride = false;
            EmitChange();
        }

        /// <summary>0=08:00, 1=12:00, 2=18:00, 3=02:00.</summary>
        public void Preset(int kind)
        {
            switch (kind)
            {
                case 0: SetOverride(8, 0); break;
                case 1: SetOverride(12, 0); break;
                case 2: SetOverride(18, 0); break;
                case 3: SetOverride(2, 0); break;
                default: ClearOverride(); break;
            }
        }

        void Update()
        {
            // 인스펙터 직접 편집 감지 — 라이브 변경 시 이벤트 발화.
            if (!_initialized || _lastOverride != useOverride ||
                (_lastOverride && (_lastH != overrideHour || _lastM != overrideMinute)))
            {
                _initialized = true;
                EmitChange();
            }
        }

        void EmitChange()
        {
            _lastOverride = useOverride;
            _lastH = overrideHour;
            _lastM = overrideMinute;
            OnTimeChanged?.Invoke();
        }
    }
}

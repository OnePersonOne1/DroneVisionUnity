using System.Collections.Generic;

namespace DroneSim.Flight.UnityAdapter
{
    /// <summary>모든 DroneAgent 의 전역 레지스트리. ID 기반 lookup + 자동 ID 발급.
    /// 명령 API, UI 선택 시스템, 미니맵·전략뷰 표시 모두 여기를 참조.</summary>
    public static class DroneRegistry
    {
        static readonly Dictionary<string, DroneAgent> _byId = new Dictionary<string, DroneAgent>();
        static readonly List<DroneAgent> _all = new List<DroneAgent>();
        static int _nextSerial = 0;

        public static IReadOnlyList<DroneAgent> All => _all;
        public static int Count => _all.Count;

        public static void Register(DroneAgent a)
        {
            if (a == null) return;
            if (string.IsNullOrEmpty(a.agentId) || _byId.ContainsKey(a.agentId))
                a.agentId = NextDefaultId();
            _byId[a.agentId] = a;
            if (!_all.Contains(a)) _all.Add(a);
        }

        public static void Unregister(DroneAgent a)
        {
            if (a == null) return;
            if (!string.IsNullOrEmpty(a.agentId)) _byId.Remove(a.agentId);
            _all.Remove(a);
        }

        public static DroneAgent Get(string id)
        {
            _byId.TryGetValue(id, out var a);
            return a;
        }

        public static string NextDefaultId()
        {
            while (true)
            {
                string id = $"drone_sim_obj_{_nextSerial++}";
                if (!_byId.ContainsKey(id)) return id;
            }
        }
    }
}

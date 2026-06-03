using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DroneSim.SITL.EditorTools
{
    /// <summary>
    /// SITL 컴포넌트를 SampleScene 에 부착하는 에디터 유틸 (batchmode -executeMethod 로 호출).
    /// 손으로 .unity YAML 을 편집하지 않고 Unity 가 직접 씬을 쓰게 해 fileID/GUID 무결성을 보장.
    /// 멱등 — 이미 있으면 중복 추가하지 않음.
    ///
    /// 실행:
    ///   Unity -batchmode -nographics -projectPath &lt;proj&gt; \
    ///         -executeMethod DroneSim.SITL.EditorTools.SitlSceneSetup.Run -quit -logFile -
    /// </summary>
    public static class SitlSceneSetup
    {
        const string ScenePath = "Assets/Scenes/SampleScene.unity";
        const string ManagerName = "SITL Manager";

        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var existing = Object.FindObjectOfType<SitlDroneSpawner>();
            GameObject go;
            if (existing != null)
            {
                go = existing.gameObject;
                Debug.Log($"[SitlSetup] 기존 SitlDroneSpawner 재사용: {go.name}");
            }
            else
            {
                go = GameObject.Find(ManagerName);
                if (go == null)
                {
                    go = new GameObject(ManagerName);
                    Debug.Log($"[SitlSetup] '{ManagerName}' GameObject 생성");
                }
            }

            if (go.GetComponent<SitlDroneSpawner>() == null)
            {
                go.AddComponent<SitlDroneSpawner>();
                Debug.Log("[SitlSetup] SitlDroneSpawner 부착");
            }
            if (go.GetComponent<SitlControlInput>() == null)
            {
                go.AddComponent<SitlControlInput>();
                Debug.Log("[SitlSetup] SitlControlInput 부착");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            bool ok = EditorSceneManager.SaveScene(scene);
            Debug.Log($"[SitlSetup] 씬 저장 {(ok ? "성공" : "실패")}: {ScenePath}");
            AssetDatabase.SaveAssets();
        }
    }
}

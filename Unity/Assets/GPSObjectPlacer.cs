using UnityEngine;

public class GPSObjectPlacer : MonoBehaviour
{
    public GameObject targetObject;

    void Start()
    {
        // Unity 원점으로 사용할 실제 GPS 좌표
        GPSEncoder.SetLocalOrigin(new Vector2(37.4480f, 126.6530f));

        // 배치하고 싶은 대상 GPS 좌표
        float lat = 37.4485f;
        float lon = 126.6540f;

        // GPS 좌표를 Unity 좌표로 변환
        Vector3 unityPosition = GPSEncoder.GPSToUCS(lat, lon);

        // 오브젝트 위치 적용
        targetObject.transform.position = unityPosition;
    }
}
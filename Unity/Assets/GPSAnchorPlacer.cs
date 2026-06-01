using UnityEngine;

public class GPSAnchorPlacer : MonoBehaviour
{
    [Header("기준이 되는 Unity 오브젝트")]
    public Transform anchorObject;

    [Header("anchorObject의 실제 GPS 좌표")]
    public float anchorLatitude = 35.576000f;
    public float anchorLongitude = 129.189000f;

    [Header("배치할 대상 오브젝트")]
    public GameObject targetObject;

    [Header("targetObject의 실제 GPS 좌표")]
    public float targetLatitude = 35.576100f;
    public float targetLongitude = 129.189200f;

    [Header("Y 높이 유지 여부")]
    public bool keepTargetY = true;

    void Start()
    {
        if (anchorObject == null)
        {
            Debug.LogError("Anchor Object가 비어 있습니다.");
            return;
        }

        if (targetObject == null)
        {
            Debug.LogError("Target Object가 비어 있습니다.");
            return;
        }

        // 1. 기준 오브젝트의 실제 GPS 좌표를 로컬 원점으로 설정
        GPSEncoder.SetLocalOrigin(new Vector2(anchorLatitude, anchorLongitude));

        // 2. 대상 GPS 좌표를 기준점 대비 Unity 좌표로 변환
        Vector3 relativeUnityPosition = GPSEncoder.GPSToUCS(
            targetLatitude,
            targetLongitude
        );

        // 3. 기준 오브젝트의 Unity 위치에 상대 좌표를 더함
        Vector3 finalPosition = anchorObject.position + relativeUnityPosition;

        // 4. 높이값 처리
        if (keepTargetY)
        {
            finalPosition.y = targetObject.transform.position.y;
        }

        // 5. 최종 위치 적용
        targetObject.transform.position = finalPosition;

        Debug.Log("GPS 변환 완료");
        Debug.Log("기준 오브젝트 위치: " + anchorObject.position);
        Debug.Log("GPS 상대 Unity 좌표: " + relativeUnityPosition);
        Debug.Log("최종 Unity 위치: " + finalPosition);
    }
}
using UnityEngine;

[ExecuteAlways]
public class UnityToGPSConverter : MonoBehaviour
{
    [Header("기준이 되는 Unity 오브젝트")]
    public Transform anchorObject;

    [Header("anchorObject의 실제 GPS 좌표")]
    public float anchorLatitude = 37.384312f;
    public float anchorLongitude = 126.655307f;

    [Header("4326 좌표로 변환할 대상 오브젝트")]
    public Transform targetObject;

    [Header("변환 결과 - EPSG:4326")]
    public float resultLatitude;
    public float resultLongitude;

    [Header("QGIS 입력용 좌표")]
    public float qgisX_Longitude;
    public float qgisY_Latitude;

    [Header("Unity 상대 좌표")]
    public Vector3 relativePosition;

    [Header("중심점 기준 사용")]
    public bool useRendererCenter = true;

    void Update()
    {
        ConvertUnityToGPS();
    }

    public void ConvertUnityToGPS()
    {
        if (anchorObject == null || targetObject == null)
        {
            return;
        }

        GPSEncoder.SetLocalOrigin(new Vector2(anchorLatitude, anchorLongitude));

        Vector3 anchorPoint = GetPoint(anchorObject);
        Vector3 targetPoint = GetPoint(targetObject);

        relativePosition = targetPoint - anchorPoint;

        Vector2 gps = GPSEncoder.USCToGPS(relativePosition);

        resultLatitude = gps.x;
        resultLongitude = gps.y;

        qgisX_Longitude = gps.y;
        qgisY_Latitude = gps.x;
    }

    Vector3 GetPoint(Transform obj)
    {
        if (useRendererCenter)
        {
            Renderer renderer = obj.GetComponent<Renderer>();

            if (renderer != null)
            {
                return renderer.bounds.center;
            }
        }

        return obj.position;
    }
}
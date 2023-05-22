using UnityEngine;
public struct TRS_Snapshot
{
    public Vector3 position;
    public Quaternion rotation;

    public Vector3 scale;

    public TRS_Snapshot(Vector3 t, Quaternion r, Vector3 s)
    {
        position = t;
        rotation = r;
        scale = s;
    }
}
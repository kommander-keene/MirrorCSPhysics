using UnityEngine;
public struct TRS_Snapshot
{
    public Vector3 position;
    // public Vector3 velocity; // First order approximation
    // public Quaternion rotation;

    // public Vector3 scale;

    // public TRS_Snapshot(Vector3 t, Quaternion r, Vector3 s)
    // {
    //     position = t;
    //     rotation = r;
    //     scale = s;
    // }

    public TRS_Snapshot(Vector3 t)
    {
        position = t;
        // velocity = v;
    }
}
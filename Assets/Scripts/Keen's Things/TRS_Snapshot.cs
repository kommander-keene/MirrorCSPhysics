using UnityEngine;
public struct TRS_Snapshot
{
    public Vector3 position;
    public Vector3 velocity;


    public Quaternion rotation;
    public Vector3 angVel;

    public TRS_Snapshot(Vector3 t, Vector3 v, Quaternion r, Vector3 a)
    {
        position = t;
        velocity = v;

        rotation = r;
        angVel = a;
    }


}
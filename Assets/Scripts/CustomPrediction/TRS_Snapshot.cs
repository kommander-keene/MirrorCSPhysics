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

    /// <summary>
    /// Creates a snapshot representing the delta states between two snapshots in time.
    /// </summary>
    /// <param name="start"> Snapshot that occurs earlier in time</param>
    /// <param name="end"> Snapshot that occurs later in time</param>
    /// <returns></returns>
    public static TRS_Snapshot Delta(TRS_Snapshot start, TRS_Snapshot end)
    {
        return new TRS_Snapshot(
        end.position - start.position,
        end.velocity - start.velocity,
        end.rotation * Quaternion.Inverse(start.rotation),
        end.angVel - start.angVel);
    }


}
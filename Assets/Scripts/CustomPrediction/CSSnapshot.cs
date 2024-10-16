using UnityEngine;
public struct CSSnapshot
{
    public Vector3 position;
    public Vector3 velocity;
    public Quaternion rotation;
    public Vector3 angVel;
    // Store a reference point to the most future state that was updated using this detail.
    public int futureReferenceIndex;

    private static CSSnapshot empty = new CSSnapshot(Vector3.negativeInfinity, Vector3.negativeInfinity, Quaternion.identity, Vector3.negativeInfinity);
    public static CSSnapshot Empty()
    {
        return empty;
    }
    public CSSnapshot(Vector3 t, Vector3 v, Quaternion r, Vector3 a, int futureRef = -1)
    {
        position = t;
        velocity = v;

        rotation = r;
        angVel = a;

        futureReferenceIndex = futureRef;
    }

    /// <summary>
    /// Creates a snapshot representing the delta states between two snapshots in time.
    /// </summary>
    /// <param name="start"> Snapshot that occurs earlier in time</param>
    /// <param name="end"> Snapshot that occurs later in time</param>
    /// <returns></returns>
    public static CSSnapshot Delta(CSSnapshot start, CSSnapshot end)
    {
        if (start.Equals(end) && end.Equals(Empty()))
        {
            return Empty();
        }
        return new CSSnapshot(
        end.position - start.position,
        end.velocity - start.velocity,
        end.rotation * Quaternion.Inverse(start.rotation),
        end.angVel - start.angVel);
    }


}
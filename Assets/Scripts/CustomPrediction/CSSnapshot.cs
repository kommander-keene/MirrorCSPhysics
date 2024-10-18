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

    public override bool Equals(object obj)
    {
        CSSnapshot snapshotB = (CSSnapshot)obj;
        return snapshotB.position.Equals(position) && snapshotB.velocity.Equals(velocity) && snapshotB.rotation.Equals(rotation) && snapshotB.angVel.Equals(angVel);
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
    /// <summary>
    /// Adds snapshot A and B to perform
    /// </summary>
    /// <param name="A"></param>
    /// <param name="B"></param>
    /// <returns></returns>
    public static CSSnapshot Update(CSSnapshot A, CSSnapshot B)
    {
        if (A.Equals(B) && A.Equals(Empty()))
        {
            return Empty();
        }
        return new CSSnapshot(
        A.position + B.position,
        A.velocity + B.velocity,
        A.rotation * B.rotation,
        A.angVel + B.angVel);
    }




}
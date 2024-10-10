using System;
using Mirror;
using UnityEngine;

// Reduced snapshots for storing rollback
public struct CSRBSnapshot
{
    public Vector3 position;
    public Quaternion rotation;
    public double timestamp;

    public CSRBSnapshot(Vector3 position, Quaternion rotation, double timestamp)
    {
        this.position = position;
        this.rotation = rotation;
        this.timestamp = timestamp;
    }
    /// <summary>
    /// Generates an interpolated position + time that is in between positions A and B
    /// </summary>
    /// <param name="A"></param>
    /// <param name="B"></param>
    /// <param name="to"></param>
    /// <returns></returns>
    public static CSRBSnapshot Interpolate(CSRBSnapshot A, CSRBSnapshot B, double to)
    {
        double newTime = Mathd.LerpUnclamped(A.timestamp, B.timestamp, Mathd.Clamp(to, 0.0, 1.0));
        Vector3 newPosition = Vector3.Lerp(A.position, B.position, (float)to);
        Quaternion newRotation = Quaternion.Lerp(A.rotation, B.rotation, (float)to);

        return new CSRBSnapshot(newPosition, newRotation, newTime);

    }
    public static CSRBSnapshot Create(Transform target)
    {
        Vector3 position = target.position;
        Quaternion rotation = target.rotation;
        double time = NetworkTime.time;

        return new CSRBSnapshot(position, rotation, time);
    }

    public static CSRBSnapshot Null()
    {
        return new CSRBSnapshot(Vector3.zero, Quaternion.identity, double.NaN);
    }
}

/// <summary>
/// Defines a queue stores states at certain timestamps.
/// Used to perform server-side rollbacks.
/// 
/// Queue size should be extremely small to prevent bloat and computer overworking
/// 
/// Ideally:
/// Add is O(1)
/// Query is O(log(n)) based off server time
/// Remove is O(1)
/// </summary>
using UnityEngine;
using System.Collections;
using System;
using Mirror;
public struct ContinuousQueue
{
    ArrayList temporal; // this list is automatically sorted due to time
    double maxLatency;

    public ContinuousQueue(double maxSecondsOfLag)
    {
        temporal = new ArrayList();
        maxLatency = maxSecondsOfLag;
    }

    public void DebugQueue()
    {
        if (temporal != null || temporal.Count == 0)
        {
            string c = "";
            foreach (CSRBSnapshot e in temporal)
            {
                c += $"{e.timestamp}: {e.position} {e.rotation}";
                Debug.Log(c);
            }
        }
    }
    /// <summary>
    /// enqueues a new snapshot and removes the latest snapshot if it is beyond the max seconds of lag
    /// </summary>
    /// <param name="s"></param>
    public void Enqueue(CSRBSnapshot s)
    {
        temporal.Add(s);
        CSRBSnapshot element = Get(0);
        double deltaDelay = NetworkTime.time - element.timestamp;
        if (deltaDelay > maxLatency)
            temporal.RemoveAt(0);
    }
    /// <summary>
    /// Dequeues oldest snapshot
    /// </summary>
    /// <returns></returns>
    public CSRBSnapshot Dequeue()
    {
        CSRBSnapshot returnal = Get(0);
        temporal.RemoveAt(0);
        return returnal;
    }
    public CSRBSnapshot Get(int index)
    {
        return (CSRBSnapshot)temporal[index];
    }
    /// <summary>
    /// Does an binary search to find the closest time this time.
    /// Note that is not REALLY the element with the closest time that is returned, because the function rounds downwards.
    /// </summary>
    /// <param name="targerTime"></param>
    public int Find(double targetTime)
    {
        int left = 0;
        int right = temporal.Count - 1;

        int bIdx = 0;
        double bDist = double.MaxValue;

        while (left <= right)
        {
            int mid = (int)((left + right) / 2);
            double midTime = Get(mid).timestamp;
            if (midTime == targetTime)
            {
                return mid;
            }
            else if (midTime < targetTime)
            {
                left = mid + 1;
            }
            else
            {
                right = mid - 1;
            }
            // Approximation
            if (midTime < targetTime && bDist > (targetTime - midTime))
            {
                bIdx = mid;
                bDist = targetTime - midTime;
            }
        }
        return bIdx;
    }
    /// <summary>
    /// Does an binary search to find the closest time this time.
    /// Performs binary search and then interpolates!
    /// </summary>
    /// <param name="targerTime"></param>
    public CSRBSnapshot FindAndInterp(double targetTime)
    {
        int bIdx = Find(targetTime);
        CSRBSnapshot A = Get(bIdx);
        if (bIdx < temporal.Count - 1)
        {
            CSRBSnapshot B = Get(bIdx + 1);
            double to = (targetTime - A.timestamp) / (B.timestamp - A.timestamp);
            return CSRBSnapshot.Interpolate(A, B, to);
        }
        CSRBSnapshot nullStruct = new(Vector3.zero, Quaternion.identity, double.NaN);
        return nullStruct;
    }
}

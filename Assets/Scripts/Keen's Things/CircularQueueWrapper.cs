using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;
public struct CircularQueueWrapper
{
    List<InputCmd> list;
    int capacity;
    public CircularQueueWrapper(int cap)
    {
        capacity = cap;
        list = new List<InputCmd>();
    }
    public void DebugQueue()
    {
        if (list == null || list.Count == 0)
        {
            return;
        }
        string c = "";
        foreach (var e in list)
        {
            c += $"{e.seq}({e.ticks}) ";
        }
        Debug.Log(c);
    }

    public void Enqueue(InputCmd cmd)
    {
        list.Insert(0, cmd);
        if (list.Count > capacity)
        {
            list.RemoveAt(list.Count - 1);
        }
    }
    public InputCmd Dequeue()
    {
        InputCmd l = list[list.Count - 1];
        list.RemoveAt(list.Count - 1);
        return l;
    }

    public int Count()
    {
        return list.Count;
    }

    public InputCmd Get(int i)
    {
        // This returns a reference!
        return list[i];
    }


    public InputCmd[] CommandArray()
    {
        return list.ToArray();
    }
}
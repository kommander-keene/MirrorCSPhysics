using System;
using System.Diagnostics.CodeAnalysis;
using UnityEngine;

public struct InputGroup
{
    public InputCmd[] commands;
    public int minLength, capacity;
    public InputGroup(int capacity)
    {
        commands = new InputCmd[capacity];
        this.capacity = capacity;
        minLength = -1;
    }
    public static InputGroup EmptyGroup(uint seqNumber)
    {
        InputGroup IMPT = new InputGroup(1);
        IMPT.commands[0] = InputCmd.Empty();
        IMPT.commands[0].seq = seqNumber;
        return IMPT;
    }
    public void Fill(InputCmd[] Inputs)
    {
        int m = Inputs.Length < capacity ? Inputs.Length : capacity;
        for (int i = 0; i < m; i++)
        {
            commands[i] = Inputs[i];
        }
        minLength = m;
    }
    public void DebugGroup()
    {
        string c = "";
        foreach (var e in commands)
        {
            c += $"{e.seq}({e.ticks}) ";
        }
        Debug.Log(c);
    }
    public InputCmd Get(int i)
    {
        return commands[i];
    }
    public void Update(int i, InputCmd cmd)
    {
        commands[i] = cmd;
    }
    public void Update(int i, int ticks)
    {
        commands[i].ticks = ticks;
    }
    public int Count()
    {
        return minLength;
    }
    public InputCmd Recent()
    {
        return Get(0);
    }
    public double Timestamp()
    {
        return Get(0).seq;
    }
    public InputCmd SearchNext(double timestamp)
    {
        // TO IMPLEMENT: does binary search to find the timestep and then returns the next one
        return Get(0);
    }
}
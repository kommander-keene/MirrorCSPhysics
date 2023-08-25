public struct InputCmd
{
    public uint seq; // Used to match position IDs
    public float axis1, axis2, axis3;
    public int ticks; // tick . duration

    public bool canJump;
    public static InputCmd Empty()
    {
        // Return an empty command
        InputCmd empty = new InputCmd();
        empty.axis1 = 0;
        empty.axis2 = 0;
        return empty;
    }

    public static bool CmpActions(InputCmd a, InputCmd b)
    {
        return a.axis1 == b.axis1 && a.axis2 == b.axis2 && a.axis3 == b.axis3
        && a.canJump == b.canJump;
    }
}


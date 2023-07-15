public struct InputCmd
{
    public double timestamp; // Used to match position IDs
    public float axis1, axis2;
    public int ticks; // tick . duration



    public static InputCmd Empty()
    {
        // Return an empty command
        InputCmd empty = new InputCmd();
        empty.axis1 = 0;
        empty.axis2 = 0;
        return empty;
    }
}


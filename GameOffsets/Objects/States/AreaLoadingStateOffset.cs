namespace GameOffsets.Objects.States
{
    using System;
    using System.Runtime.InteropServices;

    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct AreaLoadingStateOffset
    {
        [FieldOffset(0x770)] public int IsLoading; // save the structure data before, during and after area change loading screen
        // 0.5.5f: the timer advances across loading screens and this row matches the loaded area.
        [FieldOffset(0xE20)] public uint TotalLoadingScreenTimeMs;
        [FieldOffset(0xEA0)] public IntPtr CurrentAreaDetailsPtr; // WorldArea.dat row.
    }
}

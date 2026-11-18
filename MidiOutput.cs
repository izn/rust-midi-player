using System.Runtime.InteropServices;

namespace RustMidiPlayer;

internal sealed class MidiOutput : IDisposable
{
    private IntPtr handle;

    public MidiOutput(uint deviceId)
    {
        var result = midiOutOpen(out handle, deviceId, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0)
            throw new InvalidOperationException($"Could not open the MIDI output (error {result}).");
    }

    public static IReadOnlyList<MidiDevice> EnumerateDevices()
    {
        var devices = new List<MidiDevice>();
        var count = midiOutGetNumDevs();

        for (uint id = 0; id < count; id++)
        {
            var caps = new MidiOutputCapabilities();
            var result = midiOutGetDevCaps(id, ref caps, (uint)Marshal.SizeOf<MidiOutputCapabilities>());
            if (result == 0)
                devices.Add(new MidiDevice(id, caps.Name ?? string.Empty));
        }

        return devices;
    }

    public void SendNote(byte command, byte note, byte velocity)
    {
        SendShortMessage(command, note, velocity);
    }

    public void AllNotesOff()
    {
        for (byte channel = 0; channel < 16; channel++)
            SendShortMessage((byte)(0xB0 | channel), 123, 0);
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;

        try
        {
            AllNotesOff();
        }
        finally
        {
            midiOutReset(handle);
            midiOutClose(handle);
            handle = IntPtr.Zero;
        }
    }

    private void SendShortMessage(byte status, byte data1, byte data2)
    {
        var message = (uint)(status | (data1 << 8) | (data2 << 16));
        var result = midiOutShortMsg(handle, message);
        if (result != 0)
            throw new InvalidOperationException($"Could not send MIDI data (error {result}).");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MidiOutputCapabilities
    {
        public ushort Manufacturer;
        public ushort Product;
        public uint DriverVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string? Name;

        public ushort Technology;
        public ushort Voices;
        public ushort Notes;
        public ushort ChannelMask;
        public uint Support;
    }

    [DllImport("winmm.dll")]
    private static extern uint midiOutGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern uint midiOutGetDevCaps(uint deviceId, ref MidiOutputCapabilities capabilities, uint size);

    [DllImport("winmm.dll")]
    private static extern uint midiOutOpen(out IntPtr outputHandle, uint deviceId, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern uint midiOutShortMsg(IntPtr outputHandle, uint message);

    [DllImport("winmm.dll")]
    private static extern uint midiOutReset(IntPtr outputHandle);

    [DllImport("winmm.dll")]
    private static extern uint midiOutClose(IntPtr outputHandle);
}

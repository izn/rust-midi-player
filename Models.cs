namespace RustMidiPlayer;

internal enum PlaybackMode
{
    ContinuePlaylist,
    PlaySelectedOnly,
    LoopCurrentSong
}

internal sealed record MidiDevice(uint Id, string Name)
{
    public override string ToString() => Name;
}

internal sealed record PlaylistEntry(string Path, bool IsFavorite)
{
    public override string ToString() => System.IO.Path.GetFileName(Path);
}

internal sealed record TimedMidiEvent(TimeSpan Time, byte Status, byte Note, byte Velocity);

internal sealed record PreparedSong(string Path, string Name, IReadOnlyList<TimedMidiEvent> Events);

internal sealed record PlaybackModeOption(PlaybackMode Mode, string Label)
{
    public override string ToString() => Label;
}

internal sealed record PlaybackUpdate(string Message, TimeSpan Remaining);

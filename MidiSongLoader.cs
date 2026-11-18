namespace RustMidiPlayer;

internal static class MidiSongLoader
{
    private static readonly TimeSpan MinimumNoteDuration = TimeSpan.FromMilliseconds(100);

    public static PreparedSong Load(string path)
    {
        try
        {
            var noteEvents = MidiFileReader.Read(path)
                .Where(IsNoteEvent)
                .ToList();

            var firstNote = noteEvents.FirstOrDefault(IsNoteOn)
                ?? throw new InvalidDataException("The file contains no playable notes.");

            var eventsFromFirstNote = noteEvents
                .Where(noteEvent => noteEvent.Time >= firstNote.Time)
                .Select(noteEvent => noteEvent with { Time = noteEvent.Time - firstNote.Time })
                .ToList();

            var playableEvents = ExtendVeryShortNotes(eventsFromFirstNote);
            return new PreparedSong(path, Path.GetFileName(path), playableEvents);
        }
        catch (Exception exception) when (!HasFileNamePrefix(exception, path))
        {
            throw new InvalidDataException($"{Path.GetFileName(path)}: {exception.Message}", exception);
        }
    }

    private static bool HasFileNamePrefix(Exception exception, string path) =>
        exception.Message.StartsWith(Path.GetFileName(path), StringComparison.Ordinal);

    private static bool IsNoteEvent(TimedMidiEvent noteEvent)
    {
        var command = noteEvent.Status & 0xF0;
        return command is 0x80 or 0x90;
    }

    private static bool IsNoteOn(TimedMidiEvent noteEvent) =>
        (noteEvent.Status & 0xF0) == 0x90 && noteEvent.Velocity > 0;

    private static List<TimedMidiEvent> ExtendVeryShortNotes(IReadOnlyList<TimedMidiEvent> events)
    {
        var noteStarts = new Dictionary<byte, Queue<TimeSpan>>();
        var adjustedEvents = new List<TimedMidiEvent>(events.Count);

        foreach (var noteEvent in events)
        {
            var isOn = IsNoteOn(noteEvent);
            var isOff = IsNoteOff(noteEvent);
            if (!isOn && !isOff)
            {
                adjustedEvents.Add(noteEvent);
                continue;
            }

            if (!noteStarts.TryGetValue(noteEvent.Note, out var starts))
            {
                starts = new Queue<TimeSpan>();
                noteStarts.Add(noteEvent.Note, starts);
            }

            var adjustedEvent = noteEvent;
            if (isOn)
            {
                starts.Enqueue(noteEvent.Time);
            }
            else if (starts.Count > 0)
            {
                var minimumOffTime = starts.Dequeue() + MinimumNoteDuration;
                if (noteEvent.Time < minimumOffTime)
                    adjustedEvent = noteEvent with { Time = minimumOffTime };
            }

            adjustedEvents.Add(adjustedEvent);
        }

        adjustedEvents.Sort((left, right) => left.Time.CompareTo(right.Time));
        return adjustedEvents;
    }

    private static bool IsNoteOff(TimedMidiEvent noteEvent)
    {
        var command = noteEvent.Status & 0xF0;
        return command == 0x80 || (command == 0x90 && noteEvent.Velocity == 0);
    }
}

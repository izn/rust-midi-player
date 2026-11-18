using System.Text;

namespace RustMidiPlayer;

internal static class MidiFileReader
{
    private const int DefaultTempoMicroseconds = 500_000;

    private sealed record RawEvent(long Tick, int Order, byte Status, byte Note, byte Velocity, int? Tempo);

    public static List<TimedMidiEvent> Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var signature = ReadFourCc(reader);
        if (signature == "RIFF")
            signature = SeekRiffMidiData(reader);

        if (signature != "MThd")
            throw new InvalidDataException("Invalid MIDI file header.");

        var headerLength = ReadInt32BigEndian(reader);
        var format = ReadUInt16BigEndian(reader);
        var trackCount = ReadUInt16BigEndian(reader);
        var division = ReadUInt16BigEndian(reader);

        SkipHeaderExtension(stream, headerLength);
        ValidateHeader(format, trackCount, division);

        var rawEvents = ReadTracks(reader, stream, trackCount);
        return ConvertToTimedEvents(rawEvents, division);
    }

    private static string SeekRiffMidiData(BinaryReader reader)
    {
        _ = reader.ReadUInt32();
        if (ReadFourCc(reader) != "RMID")
            throw new InvalidDataException("Invalid RIFF MIDI container.");

        var stream = reader.BaseStream;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunkName = ReadFourCc(reader);
            var chunkLength = reader.ReadUInt32();
            EnsureChunkFits(stream, chunkLength);

            if (chunkName == "data")
                return ReadFourCc(reader);

            stream.Position += chunkLength + (chunkLength & 1);
        }

        throw new InvalidDataException("RIFF MIDI container has no data chunk.");
    }

    private static void EnsureChunkFits(Stream stream, uint chunkLength)
    {
        var paddedLength = (long)chunkLength + (chunkLength & 1);
        if (paddedLength > stream.Length - stream.Position)
            throw new InvalidDataException("RIFF chunk exceeds the file length.");
    }

    private static void SkipHeaderExtension(Stream stream, int headerLength)
    {
        if (headerLength < 6)
            throw new InvalidDataException("Invalid MIDI header length.");

        stream.Position += headerLength - 6;
    }

    private static void ValidateHeader(ushort format, ushort trackCount, ushort division)
    {
        if (format == 2)
            throw new NotSupportedException("MIDI format 2 is not supported.");
        if (format > 2)
            throw new InvalidDataException("Invalid MIDI format type.");
        if (trackCount == 0)
            throw new InvalidDataException("The MIDI file contains no tracks.");
        if ((division & 0x8000) != 0)
            throw new NotSupportedException("SMPTE timecode MIDI files are not supported.");
        if (division == 0)
            throw new InvalidDataException("Invalid MIDI timing division.");
    }

    private static List<RawEvent> ReadTracks(BinaryReader reader, Stream stream, ushort trackCount)
    {
        var events = new List<RawEvent>();
        var eventOrder = 0;

        for (var trackIndex = 0; trackIndex < trackCount; trackIndex++)
        {
            if (ReadFourCc(reader) != "MTrk")
                throw new InvalidDataException("Invalid MIDI track header.");

            var trackLength = ReadInt32BigEndian(reader);
            if (trackLength < 0 || trackLength > stream.Length - stream.Position)
                throw new InvalidDataException("Invalid MIDI track length.");

            var trackEnd = stream.Position + trackLength;
            ReadTrack(reader, trackEnd, events, ref eventOrder);
            stream.Position = trackEnd;
        }

        return events;
    }

    private static void ReadTrack(BinaryReader reader, long trackEnd, List<RawEvent> events, ref int eventOrder)
    {
        long tick = 0;
        byte runningStatus = 0;

        while (reader.BaseStream.Position < trackEnd)
        {
            tick += ReadVariableLength(reader);
            ReadEvent(reader, trackEnd, tick, ref runningStatus, events, ref eventOrder);
            if (reader.BaseStream.Position > trackEnd)
                throw new InvalidDataException("MIDI event exceeds its track boundary.");
        }
    }

    private static void ReadEvent(
        BinaryReader reader,
        long trackEnd,
        long tick,
        ref byte runningStatus,
        List<RawEvent> events,
        ref int eventOrder)
    {
        var firstByte = reader.ReadByte();
        var status = firstByte;
        byte? firstDataByte = null;

        if (firstByte < 0x80)
        {
            if (runningStatus == 0)
                throw new InvalidDataException("Missing MIDI status byte.");

            status = runningStatus;
            firstDataByte = firstByte;
        }

        if (status == 0xFF)
        {
            ReadMetaEvent(reader, trackEnd, tick, events, ref eventOrder);
            runningStatus = 0;
            return;
        }

        if (status is 0xF0 or 0xF7)
        {
            SkipPayload(reader, ReadVariableLength(reader), trackEnd);
            runningStatus = 0;
            return;
        }

        if (status >= 0xF0)
        {
            SkipSystemEvent(reader, status, firstDataByte, trackEnd);
            runningStatus = 0;
            return;
        }

        runningStatus = status;
        ReadChannelEvent(reader, tick, status, firstDataByte, events, ref eventOrder);
    }

    private static void ReadMetaEvent(BinaryReader reader, long trackEnd, long tick, List<RawEvent> events, ref int eventOrder)
    {
        var type = reader.ReadByte();
        var length = ReadVariableLength(reader);

        if (type == 0x51 && length == 3)
        {
            var tempo = (reader.ReadByte() << 16) | (reader.ReadByte() << 8) | reader.ReadByte();
            events.Add(new RawEvent(tick, eventOrder++, 0, 0, 0, tempo));
            return;
        }

        SkipPayload(reader, length, trackEnd);
    }

    private static void ReadChannelEvent(
        BinaryReader reader,
        long tick,
        byte status,
        byte? firstDataByte,
        List<RawEvent> events,
        ref int eventOrder)
    {
        var command = status & 0xF0;
        var noteEvent = command is 0x80 or 0x90;
        var dataByteCount = command is 0xC0 or 0xD0 ? 1 : 2;
        var note = firstDataByte ?? reader.ReadByte();
        var velocity = dataByteCount == 2 ? reader.ReadByte() : (byte)0;

        if (noteEvent)
            events.Add(new RawEvent(tick, eventOrder++, status, note, velocity, null));
    }

    private static void SkipSystemEvent(BinaryReader reader, byte status, byte? firstDataByte, long trackEnd)
    {
        var dataByteCount = status switch
        {
            0xF1 or 0xF3 => 1,
            0xF2 => 2,
            _ => 0
        };

        if (firstDataByte.HasValue && dataByteCount > 0)
            dataByteCount--;

        SkipPayload(reader, dataByteCount, trackEnd);
    }

    private static void SkipPayload(BinaryReader reader, int length, long boundary)
    {
        if (length < 0 || length > boundary - reader.BaseStream.Position)
            throw new InvalidDataException("MIDI event exceeds its track boundary.");

        reader.BaseStream.Position += length;
    }

    private static List<TimedMidiEvent> ConvertToTimedEvents(List<RawEvent> rawEvents, ushort division)
    {
        rawEvents.Sort((left, right) =>
        {
            var tickOrder = left.Tick.CompareTo(right.Tick);
            return tickOrder != 0 ? tickOrder : left.Order.CompareTo(right.Order);
        });

        var events = new List<TimedMidiEvent>(rawEvents.Count);
        long previousTick = 0;
        var elapsedSeconds = 0d;
        var tempo = DefaultTempoMicroseconds;

        foreach (var rawEvent in rawEvents)
        {
            elapsedSeconds += (rawEvent.Tick - previousTick) * (double)tempo / (division * 1_000_000d);
            previousTick = rawEvent.Tick;

            if (rawEvent.Tempo is int newTempo)
            {
                tempo = newTempo;
                continue;
            }

            events.Add(new TimedMidiEvent(
                TimeSpan.FromSeconds(elapsedSeconds),
                rawEvent.Status,
                rawEvent.Note,
                rawEvent.Velocity));
        }

        return events;
    }

    private static string ReadFourCc(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));

    private static ushort ReadUInt16BigEndian(BinaryReader reader) =>
        (ushort)((reader.ReadByte() << 8) | reader.ReadByte());

    private static int ReadInt32BigEndian(BinaryReader reader) =>
        (reader.ReadByte() << 24) | (reader.ReadByte() << 16) | (reader.ReadByte() << 8) | reader.ReadByte();

    private static int ReadVariableLength(BinaryReader reader)
    {
        var value = 0;

        for (var byteIndex = 0; byteIndex < 4; byteIndex++)
        {
            var current = reader.ReadByte();
            value = (value << 7) | (current & 0x7F);
            if ((current & 0x80) == 0) return value;
        }

        throw new InvalidDataException("MIDI variable-length value exceeds four bytes.");
    }
}

using System.Diagnostics;

namespace RustMidiPlayer;

internal static class MidiPlaylistPlayer
{
    public static async Task PlayAsync(
        IReadOnlyList<PreparedSong> songs,
        int startIndex,
        uint outputDeviceId,
        Func<PlaybackMode> getMode,
        Func<int> getPauseSeconds,
        IProgress<PlaybackUpdate> progress,
        CancellationToken cancellationToken)
    {
        using var output = new MidiOutput(outputDeviceId);
        var songIndex = startIndex;

        while (songIndex < songs.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var song = songs[songIndex];
            progress.Report(new PlaybackUpdate(
                $"Playing {songIndex + 1}/{songs.Count}: {song.Name}",
                song.Events[^1].Time));

            await PlaySongAsync(song, output, cancellationToken);
            output.AllNotesOff();

            if (ShouldStopAfterSong(getMode(), songIndex, songs.Count))
                break;

            await PauseBeforeNextActionAsync(getPauseSeconds(), getMode, progress, cancellationToken);

            var mode = getMode();
            if (mode == PlaybackMode.PlaySelectedOnly)
                break;
            if (mode == PlaybackMode.LoopCurrentSong)
                continue;
            if (songIndex == songs.Count - 1)
                break;

            songIndex++;
        }

        progress.Report(new PlaybackUpdate("Playback finished.", TimeSpan.Zero));
    }

    private static bool ShouldStopAfterSong(PlaybackMode mode, int songIndex, int songCount)
    {
        if (mode == PlaybackMode.PlaySelectedOnly) return true;
        return mode == PlaybackMode.ContinuePlaylist && songIndex == songCount - 1;
    }

    private static async Task PauseBeforeNextActionAsync(
        int pauseSeconds,
        Func<PlaybackMode> getMode,
        IProgress<PlaybackUpdate> progress,
        CancellationToken cancellationToken)
    {
        if (pauseSeconds <= 0) return;

        var pause = TimeSpan.FromSeconds(pauseSeconds);
        var message = getMode() == PlaybackMode.LoopCurrentSong
            ? $"Pausing {pauseSeconds} seconds before replaying..."
            : $"Pausing {pauseSeconds} seconds before the next song...";

        progress.Report(new PlaybackUpdate(message, TimeSpan.Zero));
        await Task.Delay(pause, cancellationToken);
    }

    private static async Task PlaySongAsync(
        PreparedSong song,
        MidiOutput output,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();

        foreach (var noteEvent in song.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitUntilEventTimeAsync(noteEvent.Time, clock, cancellationToken);
            SendNoteEvent(noteEvent, output);
        }
    }

    private static Task WaitUntilEventTimeAsync(
        TimeSpan eventTime,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        var delay = eventTime - clock.Elapsed;
        return delay > TimeSpan.Zero
            ? Task.Delay(delay, cancellationToken)
            : Task.CompletedTask;
    }

    private static void SendNoteEvent(TimedMidiEvent noteEvent, MidiOutput output)
    {
        var command = (byte)(noteEvent.Status & 0xF0);
        var velocity = command == 0x90 && noteEvent.Velocity > 0
            ? (byte)Math.Max((int)noteEvent.Velocity, 90)
            : noteEvent.Velocity;

        output.SendNote(command, noteEvent.Note, velocity);
    }
}

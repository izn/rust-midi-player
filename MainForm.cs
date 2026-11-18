namespace RustMidiPlayer;

internal sealed class MainForm : Form
{
    private readonly ComboBox outputDevices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly ListBox playlist = CreateSongList();
    private readonly ListBox favoriteList = CreateSongList();
    private readonly TabControl songTabs = new() { Width = 520, Height = 180 };
    private readonly TabPage playlistTab = new("Playlist");
    private readonly TabPage favoritesTab = new("Favorites");
    private readonly Label statusLabel = new() { AutoSize = true };
    private readonly Label remainingLabel = new() { AutoSize = true };
    private readonly Button addButton = new() { Text = "Add MIDI(s)...", AutoSize = true };
    private readonly Button removeButton = new() { Text = "Remove", AutoSize = true, Enabled = false };
    private readonly Button moveUpButton = new() { Text = "↑", AutoSize = true, Enabled = false };
    private readonly Button moveDownButton = new() { Text = "↓", AutoSize = true, Enabled = false };
    private readonly Button playButton = new() { Text = "Play", AutoSize = true, Enabled = false };
    private readonly Button stopButton = new() { Text = "Stop", AutoSize = true, Enabled = false };
    private readonly ComboBox playbackMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly NumericUpDown pauseSeconds = new() { Minimum = 0, Maximum = 60, Value = 2, Width = 55 };
    private readonly System.Windows.Forms.Timer remainingTimer = new() { Interval = 250 };
    private readonly List<string> favoriteOrder = FavoritesStore.Load();
    private readonly HashSet<string> favoritePaths;

    private DateTime? currentSongEndTime;
    private CancellationTokenSource? playbackCancellation;
    private PlaylistEntry? pendingPlaybackEntry;
    private volatile int playbackModeValue = (int)PlaybackMode.ContinuePlaylist;
    private volatile int pauseSecondsValue = 2;
    private bool isPlaying;

    public MainForm()
    {
        favoritePaths = new HashSet<string>(favoriteOrder, StringComparer.OrdinalIgnoreCase);

        ConfigureWindow();
        BuildInterface();
        WireEvents();

        RefreshFavorites();
        UpdateControls();
    }

    private void ConfigureWindow()
    {
        Text = "Rust MIDI Player";
        Width = 600;
        Height = 420;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
    }

    private void BuildInterface()
    {
        var layout = CreateVerticalPanel();
        var deviceRow = CreateHorizontalPanel();
        deviceRow.Controls.Add(CreateCaption("MIDI output:"));
        deviceRow.Controls.Add(outputDevices);

        var playlistCommands = CreateHorizontalPanel();
        playlistCommands.Controls.Add(addButton);
        playlistCommands.Controls.Add(removeButton);
        playlistCommands.Controls.Add(moveUpButton);
        playlistCommands.Controls.Add(moveDownButton);

        var transport = CreateHorizontalPanel();
        transport.Controls.Add(playButton);
        transport.Controls.Add(stopButton);

        var settings = CreateHorizontalPanel();
        settings.Controls.Add(CreateCaption("Playback mode:"));
        AddPlaybackModes();
        settings.Controls.Add(playbackMode);
        settings.Controls.Add(CreateCaption("Pause between songs (sec):"));
        settings.Controls.Add(pauseSeconds);

        playlistTab.Controls.Add(playlist);
        favoritesTab.Controls.Add(favoriteList);
        songTabs.TabPages.Add(playlistTab);
        songTabs.TabPages.Add(favoritesTab);

        layout.Controls.Add(deviceRow);
        layout.Controls.Add(songTabs);
        layout.Controls.Add(playlistCommands);
        layout.Controls.Add(transport);
        layout.Controls.Add(settings);
        layout.Controls.Add(remainingLabel);
        layout.Controls.Add(statusLabel);
        Controls.Add(layout);
    }

    private void AddPlaybackModes()
    {
        playbackMode.Items.Add(new PlaybackModeOption(PlaybackMode.ContinuePlaylist, "Continue playlist"));
        playbackMode.Items.Add(new PlaybackModeOption(PlaybackMode.PlaySelectedOnly, "Play selected only"));
        playbackMode.Items.Add(new PlaybackModeOption(PlaybackMode.LoopCurrentSong, "Loop current song"));
        playbackMode.SelectedIndex = 0;
    }

    private void WireEvents()
    {
        addButton.Click += (_, _) => AddMidiFiles();
        removeButton.Click += (_, _) => RemoveSelectedEntry();
        moveUpButton.Click += (_, _) => MoveSelectedEntry(-1);
        moveDownButton.Click += (_, _) => MoveSelectedEntry(1);
        playButton.Click += async (_, _) => await PlayAsync();
        stopButton.Click += (_, _) => StopPlayback();

        playbackMode.SelectedIndexChanged += (_, _) => UpdatePlaybackMode();
        pauseSeconds.ValueChanged += (_, _) => pauseSecondsValue = (int)pauseSeconds.Value;
        remainingTimer.Tick += (_, _) => UpdateRemainingTime();
        remainingTimer.Start();

        playlist.SelectedIndexChanged += (_, _) => UpdateControls();
        favoriteList.SelectedIndexChanged += (_, _) => UpdateControls();
        playlist.DrawItem += DrawSongListItem;
        favoriteList.DrawItem += DrawSongListItem;
        playlist.MouseDown += HandleSongListMouseDown;
        favoriteList.MouseDown += HandleSongListMouseDown;
        playlist.DoubleClick += async (_, _) => await PlayAsync();
        favoriteList.DoubleClick += async (_, _) => await PlayAsync();

        songTabs.SelectedIndexChanged += (_, _) => UpdateControls();
        outputDevices.SelectedIndexChanged += (_, _) => UpdateControls();
        Load += (_, _) => RefreshOutputDevices();
        FormClosing += (_, _) => playbackCancellation?.Cancel();
        FormClosed += (_, _) => remainingTimer.Dispose();
    }

    private void RefreshOutputDevices()
    {
        outputDevices.Items.Clear();
        foreach (var device in MidiOutput.EnumerateDevices())
            outputDevices.Items.Add(device);

        var rustPortIndex = FindRustPortIndex();
        if (rustPortIndex >= 0)
            outputDevices.SelectedIndex = rustPortIndex;
        else if (outputDevices.Items.Count > 0)
            outputDevices.SelectedIndex = 0;

        statusLabel.Text = outputDevices.Items.Count == 0
            ? "No MIDI output found. Start loopMIDI and restart the app."
            : "Select the loopMIDI port used by Rust.";
        UpdateControls();
    }

    private int FindRustPortIndex()
    {
        for (var index = 0; index < outputDevices.Items.Count; index++)
        {
            if (outputDevices.Items[index] is MidiDevice device &&
                device.Name.Contains("Rust", StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    private void AddMidiFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "MIDI files (*.mid;*.midi)|*.mid;*.midi",
            Title = "Add MIDI files",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var selectedBeforeAdd = ActiveList.SelectedItem as PlaylistEntry;
        foreach (var file in dialog.FileNames)
        {
            var fullPath = Path.GetFullPath(file);
            playlist.Items.Add(new PlaylistEntry(fullPath, favoritePaths.Contains(fullPath)));
        }

        PreserveSelectionAfterAdd(selectedBeforeAdd);
        RefreshFavorites();
        UpdateControls();

        if (isPlaying)
            RestartPlaybackFromSelection();
        else
            statusLabel.Text = $"{playlist.Items.Count} MIDI file(s) in playlist.";
    }

    private void PreserveSelectionAfterAdd(PlaylistEntry? previousSelection)
    {
        if (isPlaying && previousSelection is not null)
        {
            SelectEntry(ActiveList, previousSelection.Path);
            return;
        }

        if (playlist.Items.Count > 0)
            playlist.SelectedIndex = playlist.Items.Count - 1;
    }

    private void RemoveSelectedEntry()
    {
        var activeList = ActiveList;
        var index = activeList.SelectedIndex;
        if (index < 0 || activeList.Items[index] is not PlaylistEntry entry) return;

        if (activeList == favoriteList)
            RemoveFavorite(entry.Path);
        else
            RemovePlaylistEntry(index);

        UpdateControls();
        if (isPlaying) RestartPlaybackFromSelection();
    }

    private void RemoveFavorite(string path)
    {
        favoritePaths.Remove(path);
        favoriteOrder.RemoveAll(favoritePath => PathsEqual(favoritePath, path));
        UpdatePlaylistFavoriteState(path, false);
        SaveFavorites();
        RefreshFavorites();
    }

    private void RemovePlaylistEntry(int index)
    {
        playlist.Items.RemoveAt(index);
        if (playlist.Items.Count > 0)
            playlist.SelectedIndex = Math.Min(index, playlist.Items.Count - 1);

        RefreshFavorites();
    }

    private void HandleSongListMouseDown(object? sender, MouseEventArgs e)
    {
        if (sender is not ListBox list || e.Button != MouseButtons.Left || e.X > 26) return;

        var index = list.IndexFromPoint(e.Location);
        if (index < 0 || list.Items[index] is not PlaylistEntry entry) return;

        list.SelectedIndex = index;
        ToggleFavorite(entry);
    }

    private void ToggleFavorite(PlaylistEntry entry)
    {
        var shouldFavorite = !favoritePaths.Contains(entry.Path);
        if (shouldFavorite)
            AddFavorite(entry.Path);
        else
            RemoveFavorite(entry.Path);

        UpdateControls();
    }

    private void AddFavorite(string path)
    {
        favoritePaths.Add(path);
        favoriteOrder.RemoveAll(favoritePath => PathsEqual(favoritePath, path));
        favoriteOrder.Add(path);
        UpdatePlaylistFavoriteState(path, true);
        SaveFavorites();
        RefreshFavorites();
        SelectEntry(favoriteList, path);
    }

    private void UpdatePlaylistFavoriteState(string path, bool isFavorite)
    {
        for (var index = 0; index < playlist.Items.Count; index++)
        {
            if (playlist.Items[index] is PlaylistEntry entry && PathsEqual(entry.Path, path))
                playlist.Items[index] = entry with { IsFavorite = isFavorite };
        }
    }

    private void MoveSelectedEntry(int offset)
    {
        var list = ActiveList;
        var index = list.SelectedIndex;
        var destination = index + offset;
        if (!IsValidMove(list, index, destination)) return;

        var entry = (PlaylistEntry)list.Items[index];
        MoveListItem(list, index, destination);

        if (list == favoriteList)
            MoveFavorite(entry.Path, destination);

        UpdateControls();
    }

    private static bool IsValidMove(ListBox list, int index, int destination) =>
        index >= 0 && destination >= 0 && destination < list.Items.Count;

    private static void MoveListItem(ListBox list, int index, int destination)
    {
        var item = list.Items[index];
        list.Items.RemoveAt(index);
        list.Items.Insert(destination, item);
        list.SelectedIndex = destination;
    }

    private void MoveFavorite(string path, int destination)
    {
        favoriteOrder.RemoveAll(favoritePath => PathsEqual(favoritePath, path));
        favoriteOrder.Insert(destination, path);
        SaveFavorites();
    }

    private void RestartPlaybackFromSelection()
    {
        if (ActiveList.SelectedItem is not PlaylistEntry selected)
        {
            pendingPlaybackEntry = null;
            playbackCancellation?.Cancel();
            return;
        }

        pendingPlaybackEntry = selected;
        statusLabel.Text = $"Playlist updated. Restarting from {selected}...";
        playbackCancellation?.Cancel();
    }

    private void DrawSongListItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ListBox list || e.Index < 0 || e.Index >= list.Items.Count) return;

        e.DrawBackground();
        if (list.Items[e.Index] is PlaylistEntry entry)
            DrawSongRow(e, list, entry);
        e.DrawFocusRectangle();
    }

    private static void DrawSongRow(DrawItemEventArgs e, ListBox list, PlaylistEntry entry)
    {
        var isSelected = (e.State & DrawItemState.Selected) != 0;
        var textColor = isSelected ? SystemColors.HighlightText : list.ForeColor;
        var heartColor = entry.IsFavorite && !isSelected ? Color.Crimson : textColor;

        TextRenderer.DrawText(e.Graphics, entry.IsFavorite ? "♥" : "♡", e.Font!,
            new Point(e.Bounds.Left + 5, e.Bounds.Top + 3), heartColor);
        TextRenderer.DrawText(e.Graphics, Path.GetFileName(entry.Path), e.Font!,
            new Point(e.Bounds.Left + 28, e.Bounds.Top + 3), textColor);
    }

    private void RefreshFavorites()
    {
        var selectedPath = (favoriteList.SelectedItem as PlaylistEntry)?.Path;
        favoriteList.BeginUpdate();
        favoriteList.Items.Clear();

        foreach (var path in favoriteOrder)
            favoriteList.Items.Add(new PlaylistEntry(path, true));

        favoriteList.EndUpdate();
        if (selectedPath is not null)
            SelectEntry(favoriteList, selectedPath);
    }

    private void SaveFavorites()
    {
        try
        {
            FavoritesStore.Save(favoriteOrder);
        }
        catch (Exception exception)
        {
            statusLabel.Text = "Could not save favorites: " + exception.Message;
        }
    }

    private void UpdateControls()
    {
        var list = ActiveList;
        var selectedIndex = list.SelectedIndex;

        addButton.Enabled = true;
        removeButton.Enabled = selectedIndex >= 0;
        moveUpButton.Enabled = selectedIndex > 0;
        moveDownButton.Enabled = selectedIndex >= 0 && selectedIndex < list.Items.Count - 1;
        outputDevices.Enabled = !isPlaying;
        playButton.Enabled = selectedIndex >= 0 && outputDevices.SelectedItem is MidiDevice;
        playbackMode.Enabled = true;
        pauseSeconds.Enabled = true;
        stopButton.Enabled = isPlaying;
    }

    private ListBox ActiveList => songTabs.SelectedTab == favoritesTab ? favoriteList : playlist;

    private async Task PlayAsync()
    {
        if (isPlaying)
        {
            QueuePlaybackSwitch();
            return;
        }

        if (!TryGetPlaybackRequest(out var device, out var selectedPath, out var paths)) return;

        var cancellation = playbackCancellation = new CancellationTokenSource();
        isPlaying = true;
        UpdateControls();
        statusLabel.Text = "Preparing playback...";

        try
        {
            var (songs, skippedFiles) = LoadPlaylistSongs(paths);
            if (songs.Count == 0)
                throw new InvalidDataException("No playable MIDI files were found in the playlist.");

            var startIndex = FindStartingIndex(songs, selectedPath);
            IProgress<PlaybackUpdate> progress = CreatePlaybackProgress();

            await MidiPlaylistPlayer.PlayAsync(
                songs,
                startIndex,
                device.Id,
                GetPlaybackMode,
                GetPauseSeconds,
                progress,
                cancellation.Token);

            ShowPlaybackResult(skippedFiles);
        }
        catch (OperationCanceledException)
        {
            ShowPlaybackStopped();
        }
        catch (Exception exception)
        {
            statusLabel.Text = "Playback error: " + exception.Message;
        }
        finally
        {
            FinishPlayback(cancellation);
        }
    }

    private void QueuePlaybackSwitch()
    {
        if (ActiveList.SelectedItem is not PlaylistEntry selected) return;

        pendingPlaybackEntry = selected;
        statusLabel.Text = $"Switching to {selected}...";
        playbackCancellation?.Cancel();
    }

    private bool TryGetPlaybackRequest(
        out MidiDevice device,
        out string selectedPath,
        out string[] paths)
    {
        selectedPath = string.Empty;
        paths = Array.Empty<string>();

        var list = ActiveList;
        if (list.SelectedItem is not PlaylistEntry selected || outputDevices.SelectedItem is not MidiDevice selectedDevice)
        {
            device = null!;
            return false;
        }

        device = selectedDevice;
        selectedPath = selected.Path;
        paths = list.Items.Cast<PlaylistEntry>().Select(entry => entry.Path).ToArray();
        return paths.Length > 0;
    }

    private static (List<PreparedSong> Songs, List<string> SkippedFiles) LoadPlaylistSongs(IEnumerable<string> paths)
    {
        var songs = new List<PreparedSong>();
        var skippedFiles = new List<string>();

        foreach (var path in paths)
        {
            try
            {
                songs.Add(MidiSongLoader.Load(path));
            }
            catch (Exception exception)
            {
                skippedFiles.Add(exception.Message);
            }
        }

        return (songs, skippedFiles);
    }

    private static int FindStartingIndex(IReadOnlyList<PreparedSong> songs, string path)
    {
        for (var index = 0; index < songs.Count; index++)
            if (PathsEqual(songs[index].Path, path)) return index;

        return 0;
    }

    private IProgress<PlaybackUpdate> CreatePlaybackProgress() => new Progress<PlaybackUpdate>(update =>
    {
        statusLabel.Text = update.Message;
        currentSongEndTime = update.Remaining > TimeSpan.Zero ? DateTime.Now + update.Remaining : null;
        UpdateRemainingTime();
    });

    private PlaybackMode GetPlaybackMode() => (PlaybackMode)playbackModeValue;

    private int GetPauseSeconds() => pauseSecondsValue;

    private void ShowPlaybackResult(IReadOnlyCollection<string> skippedFiles)
    {
        currentSongEndTime = null;
        statusLabel.Text = skippedFiles.Count == 0
            ? "Playback finished."
            : $"Playback finished. Skipped {skippedFiles.Count} unsupported MIDI file(s).";
        UpdateRemainingTime();
    }

    private void ShowPlaybackStopped()
    {
        currentSongEndTime = null;
        statusLabel.Text = "Stopped.";
        UpdateRemainingTime();
    }

    private void FinishPlayback(CancellationTokenSource cancellation)
    {
        if (!ReferenceEquals(playbackCancellation, cancellation)) return;

        var nextEntry = pendingPlaybackEntry;
        pendingPlaybackEntry = null;
        playbackCancellation = null;
        cancellation.Dispose();
        isPlaying = false;
        UpdateControls();

        if (nextEntry is not null && !IsDisposed && IsHandleCreated && SelectEntry(ActiveList, nextEntry.Path))
            BeginInvoke(new Action(async () => await PlayAsync()));
    }

    private void StopPlayback()
    {
        pendingPlaybackEntry = null;
        stopButton.Enabled = false;
        statusLabel.Text = "Stopping...";
        playbackCancellation?.Cancel();
    }

    private void UpdatePlaybackMode()
    {
        if (playbackMode.SelectedItem is PlaybackModeOption option)
            playbackModeValue = (int)option.Mode;
    }

    private void UpdateRemainingTime()
    {
        if (currentSongEndTime is null)
        {
            remainingLabel.Text = string.Empty;
            return;
        }

        var remaining = currentSongEndTime.Value - DateTime.Now;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        var format = remaining.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss";
        remainingLabel.Text = "Current song remaining: " + remaining.ToString(format);
    }

    private static bool SelectEntry(ListBox list, string path)
    {
        var index = FindEntryIndex(list, path);
        if (index < 0) return false;

        list.SelectedIndex = index;
        return true;
    }

    private static int FindEntryIndex(ListBox list, string path)
    {
        for (var index = 0; index < list.Items.Count; index++)
        {
            if (list.Items[index] is PlaylistEntry entry && PathsEqual(entry.Path, path))
                return index;
        }

        return -1;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static FlowLayoutPanel CreateVerticalPanel() => new()
    {
        Dock = DockStyle.Fill,
        Padding = new Padding(18),
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false
    };

    private static FlowLayoutPanel CreateHorizontalPanel() => new()
    {
        AutoSize = true,
        WrapContents = false
    };

    private static Label CreateCaption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Padding = new Padding(0, 6, 8, 0)
    };

    private static ListBox CreateSongList() => new()
    {
        Dock = DockStyle.Fill,
        DrawMode = DrawMode.OwnerDrawFixed,
        ItemHeight = 24,
        IntegralHeight = false,
        HorizontalScrollbar = true
    };
}

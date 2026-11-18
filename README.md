# Rust MIDI Player

A small Windows app that plays MIDI files through a virtual MIDI output into Rust instruments.

## Requirements

- Windows 10 or later
- Rust with MIDI Input enabled
- [loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html) with a virtual MIDI port

## Build

```powershell
dotnet build -c Release
```

To publish a standalone Windows x64 executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Add MIDI files to the playlist, select the loopMIDI port in **MIDI output**, and press **Play**.

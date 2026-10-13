# Spotify Island

A notch-style overlay for Windows 11. It stays hidden until Spotify plays, then
appears fixed to the top-centre of your screen as a small notch with the album art,
title and a progress line. Hover over it to expand: art, title, artist, progress bar
and previous / play-pause / next buttons, with a glow taken from the album art colours.
It never expands by itself (not even on song changes) and hides ~20s after pausing.

## Requirements
- Windows 10 (2004) or Windows 11
- .NET 8 SDK  https://dotnet.microsoft.com/download/dotnet/8.0
- VS Code with the "C# Dev Kit" (or "C#") extension

## Run while developing
    dotnet run

## Build a real exe
    dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish

Put `publish\SpotifyIsland.exe` somewhere permanent (e.g. C:\Tools\SpotifyIsland\),
run it, then right-click the tray icon and tick "Start with Windows".
(Do this from the published exe, not `dotnet run`, or the wrong path gets registered.)

## Tweaks
Constants at the top of MainWindow.xaml.cs: sizes, the collapse delay,
how long after pause it hides, and TargetApp ("" = react to any media app).

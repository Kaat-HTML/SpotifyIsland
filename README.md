# Spotify Island

A notch-style overlay for Windows 11, fixed to the top-centre of your screen.

* **Music**: shows the Spotify song (album art, title, progress). Hover to expand: previous / play-pause / next,
  and a glow that takes its colour from the album art. It never expands by itself, not even on song changes.
* **Weather** (Buienradar): when no music is playing the notch shows the current temperature and a rain warning.
  Hover to expand: current conditions, the next 5 days and a 2-hour rain graph for your exact location.
  While music plays, scroll the mouse wheel over the expanded notch (or click the dots on the right) to switch
  between the music page and the weather page.

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

## Settings (top of MainWindow.xaml.cs)
* `ShowWeatherWhenIdle`  false = the notch only appears while Spotify is playing
* `ManualLatitude` / `ManualLongitude`  fixed location instead of automatic detection
* `TargetApp`, sizes, timings

## Location
1. the manual coordinates, if you filled them in
2. the Windows location service (Settings > Privacy & security > Location)
3. otherwise an approximate location from your IP address (https://ipwho.is)

## Data & attribution
Weather data: Buienradar (https://www.buienradar.nl), free for non-commercial use with attribution.
The expanded weather page shows "Bron: Buienradar.nl" and links to buienradar.nl. Keep that if you share the project.

## Full-screen behaviour
The notch hides (with its shrink animation) while a real full-screen app is in front, such as YouTube in full-screen,
a game, a video player or a slideshow, and returns when you leave full-screen. A normal maximized window, like a
maximized browser, does not hide it. This is detected with Windows events, not polling, so it costs almost nothing.

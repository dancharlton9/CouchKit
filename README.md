# CouchKit

Makes a Windows PC behave like a games console on the TV, around [Playnite](https://playnite.link/)'s fullscreen mode. It's for PCs that you also manage or stream from remotely with Moonlight, through [Sunshine](https://github.com/LizardByte/Sunshine) or [Apollo](https://github.com/ClassicOldSong/Apollo).

CouchKit is a single small background app with no window. Each feature can be switched off.

## What it fixes

**Controller**
- **Hold Back + Start to quit the game.** After 1.5 seconds, CouchKit asks the game to close; after 4 seconds, it forces it closed. The controller rumbles each time. This works for windowed games, launchers and emulators, even when Playnite has focus.
- **Tap the Xbox button to switch between the game and Playnite**, like a console's Home button. Windows normally swallows the Xbox button, so on a TV PC it otherwise does nothing.

**Streaming to another screen.** When a Moonlight session connects, Sunshine and Apollo add a virtual display and make it the main screen. That normally drags Playnite and your game off the TV. CouchKit:
- Keeps Playnite and the running game on the TV. If the TV is off, it minimises them, so the remote session gets a clean desktop for management, strategy games and so on.
- Mutes Playnite and the game while a stream is connected, so their sound doesn't come through Moonlight.
- Brings Playnite back to the front when the stream ends.
- Switches sound back to the TV when the stream ends, the TV turns on, or the PC starts. Streaming hosts often restore some other device.
- Puts the TV back to its proper resolution and refresh rate whenever the set of screens changes. Windows remembers a mode per screen combination, so the TV can come back at the wrong one.

**Lighting** (optional, needs [OpenRGB](https://openrgb.org/))
- Runs a flowing gradient in your colour palette across every RGB device OpenRGB can control: fans, RAM, GPU and motherboard.
- While a game is running, switches to colours picked from that game's artwork in Playnite.

## Requirements

- Windows 10 or 11, x64.
- Playnite, used in fullscreen mode.
- An Xbox controller, or any XInput controller, for the controller features.
- For the streaming features: Sunshine or Apollo with a virtual display. Apollo's is built in; Sunshine works with a virtual display driver.
- For lighting: OpenRGB with its SDK server turned on.

## Install

1. Download `CouchKit.exe` from [Releases](../../releases), or [build it](#building), and put it in a folder of its own, for example `C:\Tools\CouchKit`.
2. Copy `couchkit.example.json` next to it as `couchkit.json` and edit it (see [Settings](#settings)). Without a settings file, CouchKit uses the defaults.
3. Make it start with Windows: press Win+R, type `shell:startup` and add a shortcut to `CouchKit.exe` in that folder.
4. In Playnite, go to **Settings → Scripts** and add these, replacing `C:\Tools\CouchKit` with your folder. They tell CouchKit which game is running and where its artwork is.

   **Execute after a game is started:**
   ```powershell
   Set-Content -LiteralPath 'C:\Tools\CouchKit\current-game.txt' -Value $Game.InstallDirectory -Encoding UTF8
   $art = @($Game.BackgroundImage, $Game.CoverImage) | Where-Object { $_ } | Select-Object -First 1
   if ($art) { Set-Content -LiteralPath 'C:\Tools\CouchKit\current-game-art.txt' -Value $PlayniteApi.Database.GetFullFilePath($art) -Encoding UTF8 }
   ```

   **Execute after exiting a game:**
   ```powershell
   [IO.File]::Delete('C:\Tools\CouchKit\current-game.txt'); [IO.File]::Delete('C:\Tools\CouchKit\current-game-art.txt')
   ```
5. In Playnite's fullscreen settings, turn off **Guide button focuses Playnite**. CouchKit handles the Xbox button instead.
6. Start CouchKit. It writes what it does to `CouchKit.log` in its folder.

**Lighting:** in OpenRGB, set any LED counts it asks for and turn on **SDK Server** (or start it with `--server`). If OpenRGB needs admin rights for your RAM, start it from a Task Scheduler task set to *Run with highest privileges* at logon, with the arguments `--server --startminimized`. CouchKit connects whenever OpenRGB is running.

## Settings

`couchkit.json` only needs the values you want to change. Anything left out keeps its default. A typical file:

```json
{
  "Display": { "TvWidth": 3840, "TvHeight": 2160, "TvRefreshRate": 120 },
  "Audio": { "TvDeviceName": "LG TV" },
  "Games": { "EmulatorFolders": [ "D:\\Emulation\\Emulators" ] }
}
```

In JSON, backslashes in paths are written twice. Restart CouchKit after editing.

| Setting | Default | What it does |
|---|---|---|
| `Controller.Enabled` | `true` | All controller features |
| `Controller.CloseHoldMs` / `KillHoldMs` | `1500` / `4000` | How long to hold Back + Start to close / force-close the game |
| `Controller.GuideButton` | `true` | Tap the Xbox button to switch between the game and Playnite |
| `Controller.GuideTapMaxMs` | `500` | Longer presses are left to Windows |
| `Display.KeepPlayniteOnTv` | `true` | Keep Playnite off the streaming display |
| `Display.KeepGameOnTv` | `true` | Keep the running game off the streaming display |
| `Display.MuteWhileStreaming` | `true` | Mute Playnite and the game while a stream is connected |
| `Display.FocusPlayniteAfterStream` | `true` | Bring Playnite to the front when a stream ends |
| `Display.TvWidth`, `TvHeight`, `TvRefreshRate` | `0` | The TV's mode, put back whenever the screens change. `0` = leave it alone |
| `Display.VirtualDisplayNames` | `Virtual`, `SudoMaker`, `IddSample`, `Parsec` | A display adapter whose name contains one of these counts as a streaming display |
| `Audio.TvDeviceName` | empty (off) | Part of the TV's name in Windows' sound settings, for example `LG TV`. Sound is switched back to it when the TV is the only screen |
| `Games.EmulatorFolders` | none | Folders your emulators run from. Emulated games don't run from their "install folder", so this is how CouchKit finds them |
| `Games.ProtectedProcesses` | none | Extra programs never treated as the game, for example `["MyLauncher"]` |
| `Lighting.Enabled` | `true` | Lighting through OpenRGB |
| `Lighting.Palette` | aqua → magenta | Colours shown while browsing, as `#rrggbb`. Shown at full saturation, because pastels look washed out on LEDs |
| `Lighting.GameColours` | `true` | Use colours from the game's artwork while it runs |
| `Lighting.BrowseCycleSeconds` / `GameCycleSeconds` | `9` / `7` | Seconds for one full sweep of the gradient |
| `Lighting.Fps` | `30` | Updates per second |
| `Lighting.Span` | `0.5` | How much of the colour loop each device shows at once. Lower values give gentler changes between LEDs |
| `Lighting.ZoneOffset` | `0.25` | How far each zone of a device lags the one before, as a share of the loop. For example, each fan channel on a fan hub, so the fans aren't all the same colour at once. `0` = all in step |
| `Lighting.OpenRgbHost` / `OpenRgbPort` | `127.0.0.1` / `6742` | Where OpenRGB's SDK server is |

To see what colours a game's art would produce, run `CouchKit.exe --palette "path\to\image.jpg"`. The result goes to `palette-test.txt`.

## How it decides what "the game" is

Playnite's game-start script writes the game's install folder to `current-game.txt`. CouchKit treats any program running from that folder as the game. For emulated games, the install folder is usually the ROM folder, so it uses whichever program is running from `EmulatorFolders`. If no Playnite game is running, Back + Start closes whatever is in front, unless that's a protected program such as Explorer, Playnite, Steam or Sunshine/Apollo.

## Building

Run `build.cmd`. It uses the C# compiler that comes with Windows (.NET Framework 4.8), so you don't need Visual Studio or an SDK. The output is `bin\CouchKit.exe`.

## Licence

[MIT](LICENSE)

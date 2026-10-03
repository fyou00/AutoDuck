# AutoDuck

Lowers Spotify's volume while other apps (YouTube, Discord, games…) play audio, then restores it. **Windows only.**

## Install

1. **Helper** – download `AutoDuck.Helper-win-x64.zip` from [Releases](../../releases), unzip it anywhere, and run `AutoDuck.Helper.exe`.
   Want it to start with Windows? Run `install-startup.ps1` (right-click → *Run with PowerShell*).
2. **Extension** – in Spotify open **Marketplace → Extensions**, search **AutoDuck**, click **Install**.
   Manual: copy `spicetify/autoduck.js` to `%APPDATA%\spicetify\Extensions`, then run
   `spicetify config extensions autoduck.js` and `spicetify apply`.

Requires [Spicetify](https://spicetify.app).

## Use

Click the **AutoDuck** button in the playback bar (or profile menu → *AutoDuck settings*). It should say **Connected**.
Play a YouTube video → Spotify drops to 30%. Stop it → Spotify returns to its previous volume after 1.5 s.

## Troubleshooting

- **Disconnected?** Make sure `AutoDuck.Helper.exe` is running and port `8765` is free (change it in `config.json` *and* in the settings).
- **App not detected?** Run `AutoDuck.Helper.exe --list` to see which apps the helper can hear.
- **Logs:** helper → `%LOCALAPPDATA%\AutoDuck\helper.log`; extension → `spicetify enable-devtools`, `spicetify apply`, then `Ctrl+Shift+I` and look for `[AutoDuck]`.

## Build the helper

```
dotnet publish helper -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

MIT License
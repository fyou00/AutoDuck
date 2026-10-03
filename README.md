# AutoDuck

Spicetify Extension untuk Windows yang **otomatis menurunkan volume Spotify** saat aplikasi lain (Chrome/YouTube, Discord, game, dll.) memutar audio, lalu **mengembalikannya** setelah audio itu berhenti.

```
┌──────────────────────────┐   WebSocket 127.0.0.1:8765   ┌────────────────────────────┐
│ AutoDuck.Helper (C#/.NET)│ ───────── audio_state ─────► │ Spicetify extension (JS)   │
│ WASAPI per-app sessions  │ ◄──────── ping/get_state ─── │ ducking, fade, settings UI │
└──────────────────────────┘                              └────────────────────────────┘
```

Hanya volume **Spotify** yang diubah (lewat `Spicetify.Player.setVolume`), bukan volume Windows.

## Struktur

```
AutoDuck/
├── spicetify/   autoduck.js (extension + config + UI), manifest.json
├── helper/      AutoDuck.Helper.csproj, Program.cs, AudioSessionMonitor.cs,
│                WebSocketServer.cs, Configuration.cs, Logger.cs, config.json
├── scripts/     install-startup.ps1, uninstall-startup.ps1
├── README.md
└── LICENSE
```

## 1. Dependency

| Kebutuhan | Keterangan |
|---|---|
| Windows 10/11 | WASAPI Core Audio |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | untuk build helper (`dotnet --version` harus 8.x) |
| Spotify desktop (bukan Microsoft Store) | |
| [Spicetify CLI](https://spicetify.app/docs/getting-started) | sudah `spicetify backup apply` minimal sekali |

NuGet `NAudio.Wasapi` terpasang otomatis saat build. Tidak perlu hak administrator.

## 2. Build helper

```powershell
cd helper
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

Hasil: `helper\publish\AutoDuck.Helper.exe` (+ `config.json`).

## 3. Tes bertahap (prototype)

**Phase 1 – daftar sesi audio** (buka YouTube/Spotify dulu):

```powershell
.\publish\AutoDuck.Helper.exe --list
# [AudioSession] chrome.exe -> ACTIVE
# [AudioSession] spotify.exe -> ACTIVE  (diabaikan)
# [AudioSession] discord.exe -> INACTIVE
```

**Phase 2 – event** (jalankan helper, lalu putar/hentikan video):

```powershell
.\publish\AutoDuck.Helper.exe --verbose
# chrome.exe STARTED AUDIO
# chrome.exe STOPPED AUDIO
```

**Phase 3 – WebSocket** (tanpa Spotify): `npx wscat -c ws://127.0.0.1:8765`
Anda akan menerima `hello` dan `audio_state`; kirim `{"type":"get_state"}` untuk meminta state lagi.

## 4. Pasang extension ke Spicetify

```powershell
copy spicetify\autoduck.js "$env:APPDATA\spicetify\Extensions\"
spicetify config extensions autoduck.js
spicetify apply
```

(Cek lokasi folder dengan `spicetify path userdata`.) Setelah Spotify reload, buka **menu profil (avatar) → "AutoDuck settings"**.

Update kode: salin ulang file lalu `spicetify apply`. Untuk development: `spicetify watch -e autoduck.js`.

## 5. Jalankan helper otomatis saat Windows startup

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-startup.ps1
```

Membuat shortcut di folder Startup (`--background` = tanpa jendela console; log di `%LOCALAPPDATA%\AutoDuck\helper.log`). Hapus dengan `scripts\uninstall-startup.ps1`.

## 6. Cara tes dengan Chrome/YouTube

1. Jalankan helper dan buka Spotify, putar lagu, atur volume (mis. 80%).
2. Buka YouTube di Chrome dan play → Spotify turun ke 30% (fade 300 ms); settings menampilkan `External Audio: Chrome`, `Status: Ducked`.
3. Pause/tutup tab YouTube → setelah *Restore Delay* (1.5 dtk) volume kembali ke nilai sebelumnya.
4. Uji kasus tepi: set Spotify 60% sebelum play (harus kembali ke 60%, bukan 80%); pause/play cepat (tidak naik-turun); ubah volume manual saat ducked; matikan helper (Spotify harus kembali normal, status Disconnected).

## 7. Konfigurasi

**Extension** (UI settings, tersimpan di LocalStorage Spotify): enable, normal/ducked volume, restore delay, fade, trigger mode (*Any* / *Selected*), centang Chrome/Firefox/Edge/Discord/Other, proses custom (`vlc.exe`), host/port, debug.

**Helper** (`helper\config.json`): `Port`, `UsePeakMeter`, `PeakThreshold`, `PeakHoldMs`, `SafetyRescanSeconds`, `IgnoredProcesses`. Restart helper setelah mengubahnya.

Perilaku penting:
- Volume sebelum ducking disimpan dan dipulihkan; *Normal Volume* hanya dipakai bila opsi "selalu kembali ke Normal Volume" aktif.
- Jika Spotify sudah lebih pelan dari Ducked Volume (termasuk 0%), volume tidak dinaikkan.
- Jika user mengubah volume manual saat ducked, nilai itu menjadi volume yang dipulihkan nanti.
- Helper/WebSocket putus → extension memulihkan volume (fail-safe) dan reconnect dengan backoff 1→10 dtk.
- Spotify/Spicetify reload saat ducked → volume sebelumnya disimpan dan dipulihkan.
- Spotify yang sedang pause tetap di-duck, supaya volumenya sudah benar saat di-play lagi.

## 8. Protocol

```jsonc
// helper → extension
{"type":"hello","version":1}
{"type":"audio_state","active":true,"process":"chrome.exe","processes":["chrome.exe"],
 "sessions":[{"process":"chrome.exe","pid":1234}],"ts":1760000000000}
{"type":"audio_state","active":false,"processes":[],"sessions":[],"ts":1760000005000}
{"type":"pong"}
// extension → helper
{"type":"get_state"}   {"type":"ping"}
```

## 9. Debugging WebSocket

1. Helper jalan? Cek `Get-Process AutoDuck.Helper` dan `%LOCALAPPDATA%\AutoDuck\helper.log`.
2. Port terpakai? `netstat -ano | findstr :8765` → ubah `Port` di `config.json` **dan** host/port di settings extension.
3. Tes tanpa Spotify: `npx wscat -c ws://127.0.0.1:8765`. Jika berhasil, masalah ada di sisi Spotify.
4. Buka DevTools Spotify: `spicetify enable-devtools` → `spicetify apply` → `Ctrl+Shift+I`. Aktifkan "Debug log" di settings dan cari prefix `[AutoDuck]`.
5. Error CSP (`Refused to connect ... Content Security Policy`) di console: coba host `localhost` di settings; bila tetap diblokir, versi Spotify Anda membatasi `connect-src` dan perlu ditambahkan ke CSP, atau gunakan `spicetify` terbaru.
6. Firewall: koneksi loopback tidak melewati firewall; helper tidak mendengarkan di jaringan.
7. Sesi tidak terdeteksi: jalankan `--list` / `--verbose`.

## 10. Cara kerja Windows Audio Session API

Setiap *render endpoint* (speaker/headset) punya `IAudioSessionManager2`. Setiap aplikasi yang membuka stream audio mendapat **audio session** (`IAudioSessionControl`) dengan PID-nya, sehingga kita tahu **aplikasi mana** yang memutar suara, bukan sekadar apakah device dipakai.

- `IAudioSessionControl.GetState` → `Active` (ada stream yang mengalir), `Inactive`, atau `Expired`.
- `IAudioSessionEvents.OnStateChanged` memberi tahu saat sesi berubah Active ↔ Inactive; **tanpa polling**.
- `IAudioSessionNotification.OnSessionCreated` memberi tahu saat aplikasi membuka sesi baru.
- `IMMNotificationClient` memberi tahu saat device ditambah/dicabut/diganti → helper membangun ulang daftar sesi.
- Helper menggabungkan sesi per nama proses (Chrome punya banyak sesi), membuang Spotify, lalu mengirim state ke extension. Event dari beberapa sesi digabung (debounce 40 ms).

Keterbatasan: `Active` berarti stream terbuka, belum tentu terdengar. Beberapa aplikasi (mis. Discord di voice channel) menjaga sesi tetap Active saat hening, dan Chrome bisa menahan sesi beberapa detik setelah pause. Jika mengganggu, set `"UsePeakMeter": true` di `config.json`: untuk sesi yang Active saja, helper membaca peak meter tiap 250 ms dan menganggap hening sebagai tidak aktif. Ini satu-satunya polling, dan nonaktif secara default.

Helper juga menjalankan scan pengaman tiap 10 dtk (`SafetyRescanSeconds`, 0 = mati) untuk menangkap sesi yang terlewat notifikasi.

## Catatan keamanan

WebSocket hanya listen di `127.0.0.1`. Data yang dikirim hanya nama proses yang sedang mengeluarkan audio; helper tidak menerima perintah yang mengubah apa pun.

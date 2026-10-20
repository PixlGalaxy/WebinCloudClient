# Webin Cloud for Windows

The desktop app for [Webin Cloud Server](https://github.com/PixlGalaxy/WebinCloud). It signs in to your server and shows your files in File Explorer the way OneDrive and iCloud Drive do: everything is listed, nothing takes space until you open it, and changes go both ways.

The same code builds two products, **Webin Cloud** and **Ryzhell Cloud**. They differ only in name, logo and colours, and install side by side without touching each other.

Requires Windows 10 version 1809 or later, or Windows 11.

## What it does

- **Files On-Demand.** Your files appear in File Explorer under the brand's name, with its logo, in `%USERPROFILE%\<Brand Name>`. Every folder is listed ahead of time, so the navigation pane expands the whole tree; a file downloads when something opens it.
- **Thumbnails without downloading.** Photos and videos that are only in the cloud show their thumbnail, fetched from the server, not the file.
- **Always keep on this device / Free up space.** Both File Explorer menu entries work, on files and whole folders.
- **Two-way sync.** Changes on the server reach the computer within seconds over a live connection (it retries every 10 seconds when offline). Files and folders created, edited, renamed, moved or deleted on the computer go to the server. If the server refuses (a read-only folder, say), the change is undone locally. Temporary files editors write while saving are never uploaded.
- **Home and Activity.** The window shows transfers in progress with their speed, totals since the app started, recently synced items, problems, and the log.
- **Updates.** The app checks the publisher's server every few hours and installs a new version with one click, after checking the publisher's signature and the file's SHA-256 (see [Updates](#updates)).
- **Sign-in** with the browser (recommended) or a password. It stays signed in until you sign out or disconnect the device from the website.

## Installing

Run `WebinCloud.exe` (or `RyzhellCloud.exe`). It installs itself, per user and without admin rights:

| | Webin Cloud | Ryzhell Cloud |
| --- | --- | --- |
| Program | `%LOCALAPPDATA%\Programs\WebinCloud` | `%LOCALAPPDATA%\Programs\RyzhellCloud` |
| Settings and log | `%LOCALAPPDATA%\WebinCloud` | `%LOCALAPPDATA%\RyzhellCloud` |
| Files | `%USERPROFILE%\Webin Cloud` | `%USERPROFILE%\Ryzhell Cloud` |
| Start menu | Webin Cloud | Ryzhell Cloud |

It also adds an entry to **Settings → Apps**, which uninstalls it: that signs the computer out, removes the folder from File Explorer and the app's settings, and leaves the files you downloaded in place. Running a newer exe over an installed one replaces it.

To run a build without installing it, start it with `--portable`.

## Building

Needs the .NET 9 SDK.

```powershell
# Run while developing (debug builds never install or update themselves)
dotnet build src/CloudClient -p:Brand=WebinCloud
dotnet build src/CloudClient -p:Brand=RyzhellCloud

# Release: both brands into dist/<version>/
./scripts/build.bat                    # or double-click it: asks for the version
./scripts/build.ps1 -Version 1.0.0
./scripts/build.ps1 -Brand RyzhellCloud
```

The version you give sets the folder, the version stamped on both exes (and in `CloudClient.csproj`) and what goes in the `Version.txt` files. For each brand, `dist/<version>/` gets:

| File | Content |
| --- | --- |
| `WebinCloud.exe` | the app, a single self-contained exe |
| `WebinCloudVersion.txt` | the version, e.g. `1.0.0` |
| `WebinCloudSha.txt` | the exe's SHA-256 |
| `WebinCloudSignature.txt` | the signature over the brand, version and SHA-256 |

And the same names starting with `RyzhellCloud`. During development, builds go to `artifacts/<Brand>/`, one folder per brand.

## Updates

Every copy of the app, whatever server it signs in to, takes its updates from public links on the publisher's server only. They are fixed per brand in `src/CloudClient/Updates/UpdateSource.cs`:

```
https://webincloud.itzgalaxy.com/backend/api/public/durazznoapp/DurazznoApp/Apps/Dist/WebinCloudClient/
https://webincloud.itzgalaxy.com/backend/api/public/durazznoapp/DurazznoApp/Apps/Dist/RyzhellCloudClient/
```

To publish a version, upload the files of `dist/<version>/` to the brand's folder as they are:

| File | Content |
| --- | --- |
| `WebinCloudVersion.txt` | latest version, e.g. `1.0.0` |
| `WebinCloudSha.txt` | the exe's SHA-256 |
| `WebinCloudSignature.txt` | the publisher's signature |
| `WebinCloud.exe` | the exe |

And the same names starting with `RyzhellCloud` in `RyzhellCloudClient/`. The app checks every few hours and never sends anything about the user's account there.

Before replacing anything, the app checks that:

1. the version is newer than the installed one;
2. the signature was made with the publisher's key over that brand, version and SHA-256;
3. the downloaded exe's SHA-256 matches.

The SHA-256 alone only proves the download is intact, because whoever controls the server could change both. The signature proves the publisher made the build. If any check fails, nothing is replaced and the reason shows in the app and in the log. When all pass, the exe is swapped in place and the new version starts.

### The signing key

Create it once, on the machine that publishes:

```powershell
./scripts/new-signing-key.ps1
```

This writes the private key to `%USERPROFILE%\.webincloud\client-signing-key.blob` and prints the public key, which goes in `src/CloudClient/Updates/UpdateKey.cs`. Keep a backup of the private key somewhere safe. Without it, installed apps cannot accept new updates; they would have to be reinstalled by hand with a build carrying a new public key. Never commit it.

## How branding works

- `Brand.cs` holds what differs per product: the name, the identifier, the colour palette and the thumbnail provider's COM class. It is picked with `#if BRAND_<NAME>`.
- `branding/<Brand>/` holds `icon.png`, `logo.png` and `app.ico`. The project links the right ones at build time.
- Everything per app is named after `Brand.Id`:
  - the exe and the install folder;
  - `%LOCALAPPDATA%\<Brand>`;
  - the saved sign-in in Credential Manager;
  - the start-with-Windows entry and the Settings → Apps entry;
  - the single-instance lock;
  - the sync root and the COM thumbnail provider.

  The two brands never share state.

To add a brand: add its folder under `branding/` and a `#elif` block in `Brand.cs` with a new COM class id. Then add its name to the checks in `CloudClient.csproj` and `scripts/build.ps1`.

## Signing in

The app uses the server's device API (`/backend/api/devices`):

- **Browser (default):** OAuth 2.0 authorization code with PKCE for native apps (RFC 7636, RFC 8252). The app opens `<server>/connect` and listens on `127.0.0.1` for the one-time code, which only it can redeem. The password never passes through the app.
- **Password:** `POST /devices/login`, under the same rules and rate limits as the web login.

The access token lasts an hour and is renewed automatically. The refresh token changes on every use, and the server signs the device out if an old one is ever used again. The app also signs out at once in three cases:
- you disconnect the device from the website (Settings → Devices);
- an admin disconnects it;
- the password changes.

The full protocol is in the server repository: [docs/desktop-api.md](https://github.com/PixlGalaxy/WebinCloud/blob/main/docs/desktop-api.md).

## Security

- **Tokens** live in Windows Credential Manager, encrypted for the Windows user, never in `settings.json` or the log. The password is never stored.
- **HTTPS is required** for any server reachable from the internet. Plain `http` is accepted only for local network addresses:
  - `localhost`;
  - `10.x`, `172.16–31.x`, `192.168.x`, link-local and IPv6 ULA;
  - single-label, `.local`, `.lan` or `.home.arpa` names.

  If an `http` address redirects to `https`, the app switches to the `https` one.
- **Redirects** are never followed on requests that carry the token.
- **The sign-in listener** binds only to `127.0.0.1` on a random port. It reads at most 8 KB per connection, with a 10-second deadline, checks `state`, and stops as soon as the code arrives.
- **Updates** are installed only with a valid publisher signature and a matching SHA-256 (see [Updates](#updates)).

## Layout

```
src/CloudClient/
  App.cs            start-up, install/uninstall, single instance, tray, sign-in/out, updates
  Brand.cs          per-product name, id, colours and COM class
  Api/              server client: bearer tokens, renewal, errors, live events
  Auth/             browser and password sign-in, Credential Manager storage
  Core/             settings, log, start with Windows, installer
  Sync/             sync root, Cloud Files provider, local changes, thumbnails, activity
  Updates/          update address, public key, updater
  UI/               theme (Theme.cs + Theme.xaml), controls, dialogs, tray menu
  Views/            setup and main windows (Home, Activity, Settings)
  Assets/Fonts      Rajdhani (SIL OFL 1.1)
  Assets/Lucide     Lucide icons (ISC)
branding/<Brand>/   icon.png, logo.png, app.ico
scripts/build.bat   double-click to build: asks for the version, runs build.ps1
scripts/build.ps1   publish both brands into dist/<version>/
scripts/new-signing-key.ps1   create the update signing key, once
```

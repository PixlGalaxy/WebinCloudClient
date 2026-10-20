# Webin Cloud for Windows

The desktop app for [Webin Cloud Server](https://github.com/PixlGalaxy/WebinCloud): it signs in to your server and, in the coming versions, shows your files in File Explorer the way OneDrive does, downloading each one when you open it.

The same code builds two products, **Webin Cloud** and **Ryzhell Cloud**. They differ only in name, logo and colours, and can be installed side by side.

## Status

| Part | State |
| --- | --- |
| Connect to a server, sign in with the browser or a password | Done |
| Keep the session (tokens renewed automatically, stored in Windows Credential Manager) | Done |
| Tray icon, start with Windows, sign out | Done |
| Files On-Demand in File Explorer (Cloud Files API) | Next, after the server's change feed |

Requires Windows 10 version 1809 or later, or Windows 11.

## Building

Needs the .NET 9 SDK.

```powershell
# Run while developing
dotnet build src/CloudClient -p:Brand=WebinCloud
dotnet build src/CloudClient -p:Brand=RyzhellCloud

# One self-contained exe in dist/<Brand>/<runtime>/
./scripts/build.ps1
./scripts/build.ps1 -Brand RyzhellCloud
```

`Brand` defaults to `WebinCloud`. Builds go to `artifacts/<Brand>/` and `dist/<Brand>/`, one folder per brand.

## How branding works

- `Brand.cs` holds what differs per product: the name, the identifier and the colour palette, picked with `#if BRAND_<NAME>`.
- `branding/<Brand>/` holds `icon.png`, `logo.png` and `app.ico`. The project links the right ones at build time.
- Everything per-app is named after `Brand.Id`: the exe, `%LOCALAPPDATA%\<Brand>` (settings and log), the saved sign-in in Credential Manager, the start-with-Windows entry and, later, the sync root. So the two brands never share state.

To add a brand: add its folder under `branding/`, a `#elif` block in `Brand.cs`, and its name to the checks in `CloudClient.csproj` and `scripts/build.ps1`.

## Signing in

The app uses the server's device API (`/backend/api/devices`):

- **Browser (default):** OAuth 2.0 authorization code with PKCE for native apps (RFC 7636, RFC 8252). The app opens `<server>/connect` and listens on `127.0.0.1` for the one-time code, which only it can redeem. The password never passes through the app.
- **Password:** `POST /devices/login`, under the same rules and rate limits as the web login.

The access token lasts an hour and is renewed automatically. The refresh token changes on every use, and the server signs the device out if an old one is ever used again. The app is also signed out when you disconnect the device from the website (Settings → Devices), when an admin disconnects it, or when the password changes.

The full protocol is in the server repository: [docs/desktop-api.md](https://github.com/PixlGalaxy/WebinCloud/blob/main/docs/desktop-api.md).

## Security

- **Tokens** live in Windows Credential Manager, encrypted for the Windows user, never in `settings.json` or the log. The password is never stored.
- **HTTPS is required** for any server reachable from the internet. Plain `http` is accepted only for local network addresses: `localhost`, `10.x`, `172.16–31.x`, `192.168.x`, link-local, IPv6 ULA, and single-label, `.local`, `.lan` or `.home.arpa` names. If an `http` address redirects to `https`, the app switches to the `https` one.
- **Redirects** are never followed on requests that carry the token.
- **The sign-in listener** binds only to `127.0.0.1` on a random port. It reads at most 8 KB per connection, with a 10-second deadline, checks `state`, and stops as soon as the code arrives.

## Layout

```
src/CloudClient/
  App.cs            start-up, single instance, tray, sign-in/out flow
  Brand.cs          per-product name, id and colours
  Api/              server client: bearer tokens, renewal, errors
  Auth/             browser and password sign-in, Credential Manager storage
  Core/             settings, log, start with Windows
  UI/               theme (Theme.cs + Theme.xaml), controls, dialogs, tray menu
  Views/            setup and main windows
  Assets/Fonts      Rajdhani (SIL OFL 1.1)
  Assets/Lucide     Lucide icons (ISC)
branding/<Brand>/   icon.png, logo.png, app.ico
scripts/build.ps1   publish one brand as a single exe
```

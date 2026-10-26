# Account Swap

Switch between Windows user accounts the same way you switch virtual desktops.

Press **Ctrl+Win+Right** on your last virtual desktop and you slide over to the next signed-in account. Press **Ctrl+Win+Left** on your first desktop and you slide back. On any desktop in between, the keys do exactly what Windows already does.

> **Status:** early release for Windows 11. It relies on Windows session internals, so please open an issue if it misbehaves on your build.

## What it does

Windows already lets several accounts stay signed in at once ("Fast User Switching"), each with its own apps, windows and virtual desktops. The catch is getting between them: lock the screen, pick the other user from the corner of the sign-in screen, then wait.

Account Swap treats your signed-in accounts as extra desktops sitting past either end of your virtual desktops:

```
 [ Account A: desk 1 | desk 2 | desk 3 ]  ->  [ Account B: desk 1 | desk 2 ]  ->  (back to A)
          Ctrl+Win+Right past the last desktop crosses into the next account
```

- Everything keeps running in the account you leave. Nothing is signed out.
- You land on the other account's **lock screen** and type its password as usual. No passwords are stored and nothing is unlocked for you.
- Accounts that aren't signed in are skipped, so the keys only ever move between live sessions.
- With a single signed-in account the extra keypress simply does nothing.

### Optional: "Land on nearest end desktop"

Right-click the tray icon to turn this on. When you cross into an account, it then jumps to that account's nearest end desktop (first desktop when you went right, last desktop when you went left), so all your accounts feel like one long row of desktops. When it's off (the default) you arrive wherever that account was last.

## Use cases

- **Work and personal on one PC.** Keep a work profile (work email, VPN, company apps) and a personal one signed in together, and flip between them without sign-out or session loss.
- **Shared family computer.** Each person keeps their own windows and games open, and swapping between users takes one keypress instead of a trip through the lock screen.
- **Admin and standard account.** Do everyday work as a standard user and hop to a separate administrator account for the occasional elevated task.
- **Testing as different users.** Developers and IT staff checking permissions, per-user settings or first-run behaviour can cycle through test accounts quickly.
- **Client or project separation.** Freelancers can keep each client's browser sessions, files and tools in a separate account with no cross-contamination.
- **Support and troubleshooting.** Move between an affected user's account and your own while reproducing a problem, without ending either session.

## Install

1. Download `AccountSwapSetup.exe` from the [latest release](../../releases/latest).
2. Double-click it, approve the admin prompt and click **Yes**.
3. Sign out and back in once on every account you want to use it with. After that it starts by itself at each sign-in. (The account that ran the installer starts straight away.)

**Update:** run the new installer again. **Uninstall:** Settings, Apps, Installed apps, Account Swap. Silent versions: `AccountSwapSetup.exe /quiet` and `"%ProgramFiles%\AccountSwap\Uninstall.exe" /uninstall /quiet`.

Each account that should be reachable needs to be signed in already, and the hotkey only works in accounts where the app is running. It needs Windows 11 (built and developed on 11 Pro); the registry layout it reads for virtual desktops may differ on other versions.

## Build from source

Needs only the C# compiler that ships with Windows (`csc.exe`, .NET Framework 4.8). No SDK to download.

```powershell
.\build.ps1
```

This produces `bin\AccountSwap.exe` (the app) and `bin\AccountSwapSetup.exe` (the installer, with the app embedded).

## How it works

One executable, `AccountSwap.exe`, runs in two roles. A tiny installer wraps it.

```
 Account A (console)                      SYSTEM                         Account B
 +-----------------------+   named pipe   +------------------+           +---------------+
 | Agent                 | -------------> | Service          |           | Agent         |
 |  keyboard hook        |  "SWAP +1"     |  list sessions   |           |  unlock event |
 |  desktop edge check   | <------------- |  pick neighbour  |  note     |  arrival hop  |
 |  LockWorkStation      |  "OK <id>"     |  connect console | --file--> |               |
 +-----------------------+                +------------------+           +---------------+
```

### Agent (one per signed-in account, runs as that user)

- **Key capture.** A low-level keyboard hook (`WH_KEYBOARD_LL`) watches for Ctrl+Win with Left or Right. Because the hook sits in front of the shell, it can decide whether Windows should see the key at all.
- **Edge detection.** The agent decides whether you are on the first or last virtual desktop by reading `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops`: `VirtualDesktopIDs` is a packed list of 16-byte desktop GUIDs and `CurrentVirtualDesktop` is the GUID you are on. Position in the list gives you the index. This avoids the undocumented virtual-desktop COM interfaces, which change between Windows builds.
- **Pass-through.** If you are not at the edge, the key is left alone and Windows switches desktops as normal. At the edge, the key press and its auto-repeats are swallowed and a swap is requested.
- **Start-menu guard.** Swallowing the arrow key while Win is held would make Windows open the Start menu when Win is released, so the agent injects a harmless unassigned key press (`VK 0xE8`) to cancel that. Injected input is flagged and ignored by the agent's own hook.
- **Locking behind you.** When the service accepts the request, the agent calls `LockWorkStation`, so the session you leave is never left open on the console.
- **Tray and settings.** A tray icon holds the arrival toggle, stored per account at `HKCU\Software\AccountSwap\ArrivalNearestEnd`.

### Service (one per machine, runs as SYSTEM)

- **Session ring.** `WTSEnumerateSessions` plus `WTSQuerySessionInformation` produce the list of live sessions: Active or Disconnected, with a real user name, excluding session 0 (services). Sorted by session id, this is the ring. The neighbour is picked with wrap-around.
- **Console only.** The pipe client's session id (`GetNamedPipeClientSessionId`) must equal the active console session. A remote desktop session therefore cannot move the physical screen.
- **The swap.** The target session is connected to the console with `WTSConnectSession(target, console, "", false)`, the same operation as `tscon <id> /dest:console`. Doing it from SYSTEM is what allows a session to be shown without supplying its password, which is why a service is needed. If the API call fails, the service falls back to running `tscon.exe`. The call is delayed 500 ms so the agent has time to lock its own session first.
- **Rate limit.** Requests closer than 1.5 s apart are refused so a held key cannot make it bounce between accounts.
- **IPC.** A named pipe (`\\.\pipe\AccountSwapPipe`) accessible to authenticated users and SYSTEM. The protocol is one line in (`SWAP +1` or `SWAP -1`) and one line out (`OK <session>`, `NONE`, `BUSY`, `DENIED` or `ERR`).

### Arrival hand-off

Before swapping, the service writes a short note to `%ProgramData%\AccountSwap\arrival-<session>.txt` holding the direction and a UTC timestamp. When the target account's agent sees a session-unlock notification (`WTS_SESSION_UNLOCK` through `WTSRegisterSessionNotification`), it reads and deletes the note. If the note is under 60 seconds old and the account has "Land on nearest end desktop" enabled, it sends the right number of synthetic Ctrl+Win+Left/Right presses (computed from the registry desktop list) to reach the first or last desktop. Stale notes are ignored.

### Installer

`AccountSwapSetup.exe` carries `AccountSwap.exe` as an embedded resource and requests administrator rights through its manifest. It writes the files to `%ProgramFiles%\AccountSwap`, creates and starts the `AccountSwapSvc` auto-start service (restart-on-failure), adds an `HKLM\...\Run` entry so every account's agent starts at sign-in, and registers an uninstall entry for Installed apps. A copy of itself becomes `Uninstall.exe`.

### Security notes

- The swap only ever lands on a target's lock screen. Account Swap never types, stores or bypasses a password.
- The service accepts requests only from the console session and acts only on existing sessions.
- If a session was left **unlocked** while disconnected (for example by closing a remote desktop connection), swapping to it shows it as it was, with no lock screen in between. Sessions this app leaves are locked first.
- Any local user can write the arrival note for a session. The worst effect is that a session's virtual desktop moves.

### Known limitations

- Windows only, written against Windows 11. The virtual-desktop registry layout is undocumented and may change.
- Both ends need the agent running, so every account has to be signed in once after installing.
- Accounts can only be swapped to if they are already signed in; signing in still needs the normal Windows sign-in screen.

## Project layout

```
src/Native.cs        Win32 declarations, session listing, shared helpers
src/SwapService.cs   SYSTEM service: pipe server, session ring, swap
src/Agent.cs         Per-account agent: key hook, edge check, tray, arrival
src/Program.cs       Entry point and run modes
setup/Setup.cs       Self-contained installer and uninstaller
CONTEXT.md           Glossary of the project's terms
```

Run modes of `AccountSwap.exe`: `--agent` (default), `--debug` (agent with a log at `%LOCALAPPDATA%\AccountSwap\agent.log`), `--service`, and `--sessions` (prints the live session ring).

## License

MIT. See [LICENSE](LICENSE).

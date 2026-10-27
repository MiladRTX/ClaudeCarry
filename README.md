<p align="center"><img src="assets/icon-256.png" width="128" height="128" alt="ClaudeCarry logo"></p>

<h1 align="center">ClaudeCarry</h1>

<p align="center">Move your Claude Code chats and memory to another Claude account on Windows.</p>

When you log into a different Claude account in the desktop app, your Code tab chats seem to disappear.
They aren't deleted. They're still on your PC, but the sidebar list is tied to the account. ClaudeCarry
exports them, copies the list entries to the new account, and the chats show up again.

> ClaudeCarry is an independent community tool. It is not made by or affiliated with Anthropic.

## Features

- **See everything:** every account used on this PC, with its chats, project folder, last active date and size.
- **Pick what to move:** check one chat, many chats, or chats from several accounts.
- **Export** to a single zip, or **Copy / Paste** directly between accounts on the same PC (no zip).
- **Import** with a 4-step window: choose the zip, choose the chats, choose the account, then review before anything changes.
- **Safe by design:** a backup is made first, nothing is overwritten or deleted, and login data is never read or exported.
- **One file:** `ClaudeCarry.exe` (about 1 MB). Nothing to install.
- Light and dark mode.

## Requirements

- Windows 10 or 11
- The Claude desktop app with the Code tab
- Microsoft Edge WebView2 Runtime (included with Windows 11; on Windows 10 the app tells you if it's missing)

## Quick start

1. Quit the Claude app completely (also from the tray icon).
2. Open **ClaudeCarry.exe**, click **Export everything**, and save the zip.
3. In Claude, log out, log into the new account, open the **Code** tab once, then quit Claude again.
4. In ClaudeCarry, click **Refresh**, then **Import**. Pick the zip, choose your chats and the new account, and click **Import**.
5. Start Claude. The chats are in the sidebar.

**Faster on the same PC:** select chats, click **Copy** (Ctrl+C), open the other account and click **Paste here** (Ctrl+V).

## How it works

Claude Code stores data in two places:

| Data | Location | Per account? |
|---|---|---|
| Chat transcripts and project memory | `%USERPROFILE%\.claude\projects\` | No, shared |
| Chat list shown in the sidebar | `%APPDATA%\Claude\claude-code-sessions\<accountId>\<orgId>\` | Yes |

A new account starts with an empty chat list, so ClaudeCarry only needs to copy list entries and, for zips,
the transcripts and memory that belong with them.

## What's exported

| Included | Not included |
|---|---|
| Chat transcripts, including subagent logs | `.credentials.json` and `.claude.json` (login data, never read) |
| Each project's memory (`memory\`) | Settings values whose names contain key, token, secret, password, auth, credential, cookie or bearer |
| `CLAUDE.md` and `skills\` (except `.trash`) | Caches, shell snapshots, file history, plugins |
| `settings.json`, with secrets removed | |
| Chat lists for every account on this PC | |

The memory and settings options can be turned off with the **Include memory, skills & settings** switch.

## Import and copy rules

- **Backups:** before anything changes, a backup zip is written to `Documents\ClaudeCarry-backups\`.
- **Only adds:** files that don't exist are copied in. Identical files are skipped.
- **Never overwrites:** a file that differs is left alone and listed. The one exception is `MEMORY.md`: missing lines are
  appended and nothing is removed. A different `settings.json` is saved next to yours as `settings.imported.json`.
- **Chats:** skipped if they're already in that account, their transcript is missing, or you deleted them there.
  Archived chats stay archived.
- **Claude must be closed:** ClaudeCarry won't write while the Claude app is running. Quit it and click **Check again**.
- Running the same import or paste twice is safe. The second run adds nothing.

## Moving to another PC

Export on the old PC, copy the zip and `ClaudeCarry.exe` to the new one, then follow the Quick start from step 3.
Chats only reopen correctly if the project folders are at the same paths (same Windows user name and folders).
ClaudeCarry warns you when the paths differ.

## Command line

The same tool works from a terminal, for scripting:

```bat
ClaudeCarry.exe accounts
ClaudeCarry.exe chats
ClaudeCarry.exe export [out.zip] [--chats local_aaa,local_bbb] [--no-shared]
ClaudeCarry.exe import <file.zip> [--account <accountId>\<orgId>] [--chats local_aaa,...] [--dry-run]
ClaudeCarry.exe copy --chats local_aaa,... --account <accountId>\<orgId> [--dry-run]
```

## Building from source

Clone the repo and run `build.bat`. It uses the C# compiler that comes with Windows (.NET Framework 4.8), so no SDK is needed.

```
ClaudeCarry/
├── src/        C# source: Carry.cs (core), Host.cs (window bridge), Program.cs (entry point, command line)
├── ui/         index.html, the whole interface (HTML, CSS, JavaScript in one file)
├── lib/        Microsoft WebView2 SDK files (see lib/WebView2-LICENSE.txt)
├── assets/     app icon (ClaudeCarry.ico) and README logo (icon-256.png)
└── build.bat   builds ClaudeCarry.exe
```

`build.bat` embeds `ui\index.html` and the WebView2 files into the exe, so the result is a single file.

- The first start unpacks `WebView2Loader.dll` to `%LOCALAPPDATA%\ClaudeCarry\bin\`. WebView2 keeps its cache in `%LOCALAPPDATA%\ClaudeCarry\WebView2\`.
- To work on the design, open `ui\index.html` in a browser. Outside the app it runs on sample data.
- Set `CLAUDECARRY_DEVTOOLS=1` before starting the exe to enable DevTools (F12) in the window.

## Known limits

- Chats started in a scratch folder (`%APPDATA%\Claude\scratch-workspaces\...`) keep their transcript and memory,
  but a new account uses new scratch folders, so that memory won't load automatically in new scratch chats.
- Chats stored on Claude.ai in the browser are kept on Anthropic's servers and are not covered.
- A chat that already continued under another account shows a "thinking from another organization" notice once.
  That's Claude's own rule. The chat itself is complete.
- Only Windows is supported.
- The app depends on how the Claude desktop app stores its chat list. If that changes, ClaudeCarry may need an update.

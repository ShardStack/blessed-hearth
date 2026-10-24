# Blessed Hearth - A Not So Cursed Client

[![Support on PayPal](https://img.shields.io/badge/Support-PayPal-0070ba?logo=paypal&logoColor=white)](https://paypal.me/shardstackdev)

Version 2.1.0, the C# version

An addon manager for the MMOs on CurseForge (every World of Warcraft version, The Elder Scrolls Online, Rift and Runes of Magic). It talks to CurseForge directly, so you don't need the CurseForge app.

- Reads your AddOns folder and figures out which CurseForge project each addon came from, even ones you installed by hand
- Shows your version next to the newest one for your game version, and updates one addon or all of them
- Searches CurseForge and installs addons, and offers to grab any libraries they need
- Backs up an addon before replacing or removing it
- Never refuses an addon because it "looks modified" or "incompatible"

## Support

Blessed Hearth is free and always will be. If it saves you some hassle and you'd like to chip in, you can [buy me a coffee on PayPal](https://paypal.me/shardstackdev). Thank you!

## Coming from an earlier version

This is the same app as the Python version (1.x), rewritten in C# so it's one small exe that starts instantly. It uses the very same settings folder, the same saved addon history, the same encrypted API key and the same Start with Windows entry, so it just picks up where the Python version left off. Nothing to import, and your addons, backups and per-folder history aren't touched.

Coming from even further back, when the app was called Not So Cursed Forge, it moves everything over by itself the first time it starts: your settings and saved API key (`%APPDATA%\NotSoCursedForge` becomes `%APPDATA%\BlessedHearth`), the CurseForge pane's cookies, and the Start with Windows entry, which keeps whatever on or off setting it had. Old leftovers (the old unpacked app and the old window engine's caches) get deleted.

## Running it

Run `Blessed Hearth.exe`. It's the whole app in one file, about 1.5 MB, and there's nothing to install. It needs Windows 10 or 11, which already come with everything it uses: .NET Framework 4.8 and Edge's WebView2 engine (the part of Edge that other apps can borrow to show web pages). The first time a new version runs, it copies its interface files into `%LOCALAPPDATA%\BlessedHearth\files`, which takes a blink.

If a PC is missing WebView2 (Microsoft says only a few Windows 10 PCs are), the app opens its interface in a Microsoft Edge window instead and everything still works. WebView2 itself is a free download from Microsoft: https://developer.microsoft.com/microsoft-edge/webview2/

The first screen has one step, **pick your games:** the app lists every game and WoW version it finds on this PC. Tick the ones you want, or choose a folder yourself. That's it.

There's no API key to get anymore. CurseForge approved Blessed Hearth and gave it its own key, which comes built into the exe. (Version 2.0 asked everyone to get a key of their own. If you saved one back then, it just sits unused in your settings.)

Built it yourself from the source code? Then the exe has no key in it, and the first screen asks for one, the same way 2.0 did: sign in at https://console.curseforge.com/, open **API keys**, copy your key and paste it in.

## Games and WoW versions

The picker at the top of the sidebar switches between them. Each one has its own addon list, its own update checks, and search filtered to it (for WoW, to that exact version). Add more any time from the picker's **Add a game or WoW version**. It lists every game and WoW version the app supports. The ones found on this PC show their folder and add in one click, for the rest you point it at the folder.

- **World of Warcraft:** each version (Retail, Classic Era, Anniversary, Mists Classic, Forever and so on) is separate. Under the hood each one is just its own folder (`_retail_`, `_classic_era_`, `_classic_beta_`) with its own `Interface\AddOns`. Battle.net writes the version's name into a tiny `.flavor.info` file in that folder, and that's how the app tells them apart.
- **The Elder Scrolls Online:** `Documents\Elder Scrolls Online\live\AddOns`
- **Rift:** `Documents\RIFT\Interface\AddOns`
- **Runes of Magic:** `<game folder>\Interface\AddOns`

How CurseForge recognizes hand-installed addons is known for WoW (and Runes of Magic, which uses the same `.toc` files). For ESO and Rift it hasn't been published, so the app sends a few likely fingerprints and uses whichever one CurseForge recognizes. Anything it can't match can be linked by hand.

## How it works, in plain terms

**What the app is.** One exe with three pieces inside:
- the addon engine (`src\Core`): reading addon folders, fingerprints, talking to CurseForge, installing and backing up
- a tiny web server (`src\App\Server.cs`) that only this PC can reach, handing the engine to the interface
- a window (`src\Window`) that shows the interface in `ui\index.html` using WebView2. The window has no Windows title bar of its own: the page draws one, and marks its top strip as a title bar so dragging, double clicking and Snap work like any other window.

**What an addon is on disk.** Just a folder in `AddOns` with a `.toc` file in it. The `.toc` is a text file with the addon's name, version, and the `.lua` and `.xml` files to load. Some addons are a few folders (Bagnon, Bagnon_Config and so on).

**How it recognizes addons you installed by hand.** With a fingerprint, the same trick the CurseForge app uses. For each folder it reads the `.toc`, follows every file the addon loads, strips out all the spaces, tabs and line breaks, and runs what's left through a hash (MurmurHash2). That gives one number per folder. It sends those numbers to CurseForge, which answers with something like "that number is Questie, file 1.0.0". Only the numbers get sent, never your files.

If a fingerprint doesn't match (you edited a file, or it came from somewhere else), it falls back to:
1. what this app installed before,
2. an `## X-Curse-Project-ID` line in the `.toc`, which most CurseForge addons have,
3. you: the **Link** button or the ... menu > **Link to a CurseForge project**.

**How updates work.** CurseForge tags every file with the game version it's for. The app only looks at files tagged for your version, and in Settings you can pick Release only, Release and beta, or everything including alpha. An update downloads the zip, checks it's exactly the file CurseForge describes, moves the old folders to a backup and drops the new ones in.

**Backups.** Old versions go to `Interface\AddOns_Backups\<date>_<name>`, next to AddOns rather than inside it so the game never loads them. The newest 15 are kept, you can change that in Settings. To roll back, copy the folders back into AddOns.

## Statuses

| What you see | What it means |
|---|---|
| Version arrow and an **Update** button | A newer file exists for your game version. If the left side says "unknown", the addon was installed by hand and edited, so its exact version can't be told. Updating once fixes that. |
| Up to date | You have the newest file. |
| No Forever version yet | The project exists but has nothing for this game version yet. |
| **Get beta** button | Only beta (or alpha) builds exist for this version. One click turns on beta updates for just that addon. The ... menu turns it back off. |
| **Link** button | No match yet. Pick the CurseForge project it came from. |
| Skipping updates | You turned updates off for this one. The ... menu turns them back on. |

The ... menu on each addon also has Open on CurseForge, Show in Explorer, Unlink and Remove.

## Settings worth knowing about

- **Website downloads.** CurseForge lets authors turn off downloads from apps other than its own. For those addons the app opens the file's CurseForge page right inside the window, in the spot where the details panel normally sits. Click Download if it doesn't start on its own, and the app installs it once it lands. It checks the file's size and checksum against CurseForge first, so a different file with the same name won't get installed.
- **Themes:** Settings > Appearance. Arcane (the original violet), Forever (the WoW Forever login screen), Dark (plain black and grey), Fel, Frost and Crimson.
- **Background update checks:** Settings > Running in the background. Every hour, 3, 6 or 12 hours, once a day, or never (6 hours to start). While the app is running, even tucked in the tray, it checks all your games. If new updates turn up while the window isn't in front of you, a Windows notification lists them and clicking it opens the app on that game. Each update only gets announced once, and the tray icon gets a dot while updates are waiting. Right-click the tray icon > **Check for updates now** checks right away and always answers, even if it's "No new updates".
- **Keep running in the tray:** closing the window tucks the app into the system tray (the icons by the clock). Click the icon to bring it back, right-click it and choose **Exit** to quit.
- **Start with Windows:** the app adds itself to the list of programs Windows runs when you sign in. That list is just a registry key, `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, the same one Task Manager's Startup apps tab shows. Turning it off removes the entry. **Start minimized** starts it quietly in the tray, or minimized on the taskbar if the tray option is off.
- Opening the app while it's already running just brings the running copy forward.

## Privacy and security

- **The app's CurseForge key** is built into the exe, scrambled so it isn't sitting in there as readable text. It's never in the source code or on GitHub. It's only ever sent to CurseForge's servers, and if CurseForge ever redirects a request somewhere else, the key gets left off.
- **Your own API key** (only on builds without the app's key) is stored in `%APPDATA%\BlessedHearth\config.json`, encrypted with Windows' own per-user encryption (the same thing browsers use for saved passwords). Only your Windows account on your PC can read it. It's only ever sent to CurseForge's servers. If CurseForge ever redirects a request somewhere else, the key gets left off.
- **The app's little server** only listens on 127.0.0.1, so nothing else on your network can reach it. Every request needs a random password that's made fresh each time the app starts, and requests that come in through some other domain name get turned away.
- **The interface** runs under a strict security policy: only the app's own script runs, and text from CurseForge (names, descriptions, changelogs) is always shown as plain text, never as live code. Links only open CurseForge pages (and the app's own download page on GitHub), in your normal browser.
- **Downloads** have to be https, have to match CurseForge's size and checksum, and zips that try to write outside their folder or unpack to something enormous get refused.
- **Your game settings,** SavedVariables and keybinds live in the `WTF` folder, which the app never touches.
- Everything the app remembers is in `%APPDATA%\BlessedHearth\`: the config, one state file per AddOns folder, and a small log (it never has your key in it). The interface files, the window's browser data and the CurseForge pane's cookies are in `%LOCALAPPDATA%\BlessedHearth\`. Deleting those two folders removes every trace of the app, and it doesn't touch your addons.

Run it while the game is closed, or `/reload` after updating.

## Making a release

`Build-Release.ps1` makes a folder named after the version (like `2.0.0`, or `2.0.0 (2)` if that's already there) with:

- `Blessed Hearth.exe`, the whole app in one file, ready to share. No console window pops up when it starts.
- `Source`, a clean copy of just the app's files (code and interface). It's copied from a fixed list, then scanned for API keys, email addresses and your user folder, and the build stops if it finds any.

The app's CurseForge key gets built into the exe while it's being made, and never goes into `Source` or anywhere else in the app's folder, so it can't end up on GitHub. The first time you build, the script asks you to paste the key (it doesn't show on screen) and saves it encrypted for your Windows account in `%LOCALAPPDATA%\BlessedHearth\build-tools\curseforge-app-key.dat`, so it only asks once. Under the hood it writes a tiny extra code file with the key mixed with random bytes into your temp folder, hands it to the compiler with the rest of the code, and deletes it right after. Then it double checks the key can't be found as plain text inside the finished exe.

To be upfront about it: scrambling isn't the same as locking. Someone determined could still dig the key out of the exe. That's true of every app that ships with a key, and it's how CurseForge expects approved apps to work.

Run it from the app's folder:

```
powershell -ExecutionPolicy Bypass -File .\Build-Release.ps1
```

Under the hood the build is one run of Microsoft's C# compiler. The first time, it downloads two free Microsoft packages from nuget.org (Microsoft's own package site) into `%LOCALAPPDATA%\BlessedHearth\build-tools`, about 20 MB, and reuses them after that: the C# compiler itself (Windows has an older one built in, but it only understands C# from 2012) and the WebView2 SDK, the two small DLLs that let a window host Edge's engine. Every file is checked for Microsoft's digital signature before it's used. The interface files and those DLLs get packed inside the exe.

`-SkipExe` makes just the clean source copy. `-NewKey` forgets the saved key and asks for it again, for when CurseForge gives you a new one. `-NoKey` builds an exe without the app's key, which asks each person for their own like 2.0 did. If the key is in the `BLESSED_HEARTH_API_KEY` environment variable, the script uses that instead of asking.

The fonts, Marcellus and Instrument Sans, are bundled under the SIL Open Font License (see `ui/fonts/OFL.txt`).

# Camera Sweep - notes for Claude

## Release notes
Every change a user would notice goes into `release-notes.md` as it is made: one line under ADD, CHANGE,
REMOVE or FIX, written for players (what it does for them, not how). `release.bat` shows the file in Notepad,
publishes it as a draft GitHub release, copies it to `docs/releases/<version>.md` and `CHANGELOG.md`, then empties it.

## Releasing
`release.bat` (wrapper for `scripts/release.ps1`): asks for the version (default: `Version` in
`src/CameraSweepPlugin.cs`, which it updates along with the csproj), builds, zips into `releases/` (ignored by git)
and uploads a draft release to AlbatrossLabs/HS2CameraSweeper with the zip and its SHA-256.
Token: user variable `GH_CameraSweep` (fine-grained, Contents read/write on that repo).
Zip layout: `BepInEx/plugins/HS2CameraSweep/` holding the DLL, libwebp_cs.dll, its licence files,
README.txt (from `packaging/README.txt`) and CHANGES.txt (this version's notes).

## Working copy
The user commits and pushes with GitHub Desktop. Don't rewrite history or force-push.

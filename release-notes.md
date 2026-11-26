<!--
Release notes for the next version of Camera Sweep, filled in as the code changes.
Optional: a sentence or two up here. Then one line per change under its heading ("- ..."); empty headings are left out.
release.bat opens this in Notepad before publishing, uploads it as a draft GitHub release, saves a copy to
docs\releases\<version>.md, adds it to CHANGELOG.md and empties this file for the next version.
-->

Camera Sweep 1.0: tested end to end. Shoot every camera in your Studio scene, for every outfit, pose, expression and character swap you pick, in one go.

### ADD
- Picking with Studio Browser: with Studio Browser 0.23 or later, *+ Pick* uses its Character, Outfit and Pose browsers (they open the way you've set them to open with Studio's lists). Double-click, Enter or **Add to Camera Sweep** adds the selected cards to the character's list; **Add all shown** adds everything the browser shows, with your folder, search and tag filters. Studio's own lists still work too.
- Each camera's Graphics preset: with Studio Theme 0.16.8 or later and the Graphics plugin, each camera or numbered view is shot with its Graphics preset (cameras without one get Graphics' default look), so one sweep can cover a day camera and a night camera. Your Graphics settings from before the sweep come back afterwards. On by default; the switch is under the window's capture settings.

### CHANGE
- The plugin now installs to `BepInEx\plugins\AlbatrossLabs\HS2CameraSweep\`, next to the other Albatross Labs plugins. Updating from 0.9.6 or earlier: delete the old `BepInEx\plugins\HS2CameraSweep\` folder.

### REMOVE

### FIX

# Changelog

## v0.9.6
- Blinking and breathing (AdvIK) are paused on every character while stills are shot, and put back afterwards (Image > Advanced).
- New look matching VN Deck: dark panels, neon mint borders and main button, blue titles and fold-out headers.
- Camera Sweep's own renderer is now the default capture engine (cleaner hair edges with transparency).
- The progress bar sits at the bottom of the screen; the full-screen cover is optional (off by default).
- Toolbar button asks for the bottom row of Studio's toolbar.
- Pick mode opens Studio's character list and Costume tab by itself; the pick banner moved to the bottom.
- Clothing states: columns Top | Bra | Bottom | Panties; new *half* preset (bra and panties half, top and bottom off); underwear set to half or off takes the clothes over it off.
- Expressions: built-in *ahegao* (11/11/17); steppers wrap around.
- Cameras are labelled Cam 1, Cam 2... (`cam01` in file names).
- Readable file-name token help.
- VideoExport: its window opens to show the ETA, a *VideoExport settings* button, and videos are always opaque.
- Fixes: "outfit they're wearing now" after a character swap; a shot after a card load could come out zoomed out; KKAPI error when saving the starting outfit (plugin data now kept); layout error when pressing Capture.

## v0.9.x
- Per-character variations (swap, outfits, poses, clothing states, expressions, face extras), picked from Studio's own lists.
- Built-in WebP (lossless and lossy) and Camera Sweep's own renderer next to Screencap, with an engine comparison test shot.
- Image size presets, upsampling, transparency, shadow and AO overrides, background encoding.
- File-name templates with tokens and subfolders; size and time estimates.

## v0.6
- Clothing states, character swap, image count and size estimate, easier face presets.

## v0.2
- First release: shoot every camera in a scene; outfit and expression variations; video through VideoExport.

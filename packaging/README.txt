Camera Sweep v{VERSION} for Honey Select 2 Studio
=================================================
Albatross Labs - https://github.com/AlbatrossLabs/HS2CameraSweeper

Shoots every camera in a Studio scene in one go, with optional variations per
character (character swaps, outfits, poses, clothing states, expressions, face
extras), as PNG / WebP / JPG stills or as videos through VideoExport.

REQUIREMENTS
  - Honey Select 2 with BepInEx 5 and HS2API (KKAPI) - any recent BetterRepack has both.
  - Optional: Screencap (second capture engine), VideoExport (video mode),
    AdvIK (its breathing is paused during stills).

INSTALL
  1. Close the game.
  2. Extract the zip into your Honey Select 2 folder (the one with HoneySelect2.exe).
     Everything lands in BepInEx\plugins\AlbatrossLabs\HS2CameraSweep\ :
       HS2CameraSweep.dll    the plugin
       libwebp_cs.dll        WebP encoder (libwebp 1.4.0)
       README.txt            this file
       CHANGES.txt           what's new in this version
       LICENSE-libwebp.txt / PATENTS-libwebp.txt
  3. Updating: overwrite the old files. Coming from 0.9.6 or earlier, delete the old
     BepInEx\plugins\HS2CameraSweep\ folder (the plugin now lives under AlbatrossLabs).

USE
  In Studio, press Shift+F10 or click the Camera Sweep icon on the left toolbar.
  Pick cameras, add characters and their variations, then press Capture.
  Images go to UserData\cap\CameraSweep\ (change it under Files).
  Press Esc during a sweep to cancel; the scene is put back as it was.

  Settings: F1 (Configuration Manager) > Camera Sweep.
  Log: output_log.txt in the game folder.
  Full history: CHANGELOG.md on GitHub.

LICENSES
  Camera Sweep: see the GitHub repository.
  libwebp: BSD-3-Clause, Copyright (c) Google Inc. (LICENSE-libwebp.txt, PATENTS-libwebp.txt).

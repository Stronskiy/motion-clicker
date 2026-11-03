# Motion Clicker icon

`app-icon.svg` is the original editable source: a folded action route joining a
cursor on a cobalt tile. The geometry was authored for this project. It contains
no embedded bitmap, external dependencies, fonts or third-party logo artwork.
The asset is covered by this repository's Apache-2.0 license.

A concept was explored with the built-in ChatGPT Image tool on2026-11-04, using
the brief “pearl folded motion ribbon ending in a cursor, cobalt glass tile,
transparent surround, no text”. The final vector was manually reconstructed and
the cursor direction changed to distinguish it from a growth-chart arrow. The
generated concept remains local design evidence; it is not embedded in this SVG.

Run `python3 scripts/generate-app-icons.py` on macOS with Google Chrome installed
to render the SVG, create the full native ICNS set, and create a PNG-compressed
ICO with16,24,32,48,64,128,256px entries. macOS sips/iconutil and Python stdlib
perform packaging. The temporary Chrome profile is separate from user profiles;
no user browser data or screen capture is used. Renderer versions may alter
antialiasing; this is a reproducible workflow, not a bit-identical build claim.

Normal app builds consume the committed native resources and need no browser.
`dist/app-icon.png` is a local preview. A Windows shell/taskbar visual check
requires a real Windows session and is distinct from build/resource validation.

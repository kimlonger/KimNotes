KimNotes 1.4.20 homepage visual assets

The current note, screenshot selection and task list were rendered from the
1.4.20 release EXE using ExportProductAssets.cs with synthetic content and an
isolated data directory. The offscreen renderer did not reproduce the alpha of
the small app icon; the same position was composited with the official Note icon.
The task editor is a snapshot from the 1.4.20 desktop regression using synthetic
content. The pinned image is a crop of the selected screenshot. The 3-second
homepage transition is an illustrative animation composed from these real UI
frames; it is not a screen recording. The horizontal share card was composed
from these product visuals and the official icon, without AI-generated UI.

Older hero-*.png files remain available for earlier references. The public
release ZIP and real user data are not modified by the asset exporter.

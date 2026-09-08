# HoomNote 0.8.4

- Fixed Samsung Notes image placement by reading the displayed-image binding from the fill record. Repeated references now reuse the correct image, and missing bindings no longer select an unrelated file.
- Preserved Samsung image rotation and the order of objects across layers.
- Fixed image loads losing refresh notifications after a graphics-device reset. Image-heavy pages now share a decode budget and retain their active images while rendering.
- Prevented adding or pasting an image into a different page when navigation occurs during loading. Failed image loads show an unavailable message.
- Preserved image proportions and rotation, and imported PDF transforms, during PDF export.
- Made notebook package import reject missing, duplicate, or mismatched assets. Export stops before replacing an existing package if a referenced asset is missing.
- Fixed applying notebook page settings to unloaded pages so those settings survive reopening.

Existing Samsung imports must be reimported to correct previously saved image associations. Keep the previous notebook if it contains edits made after import.

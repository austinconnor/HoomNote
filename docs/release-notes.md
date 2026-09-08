# HoomNote 0.8.5

- Fixed copying notebook objects failing before they reached the clipboard. Selected handwriting, images, shapes, and typed text can now be copied and pasted within HoomNote.
- Fixed the internal clipboard fallback after a failed Windows clipboard write. Copies remain available across notebook tabs and windows until the system clipboard changes.
- Made copied content persist after HoomNote exits when Windows accepts the clipboard flush.
- Made pasting multiple objects a single undo and redo action.
- Prevented delayed text and object paste from inserting into a different page after navigation.
- Added a visible message when copied object data cannot be read.

Existing Samsung imports must be reimported to correct previously saved image associations. Keep the previous notebook if it contains edits made after import.

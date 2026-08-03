# HoomNote 0.7.37

- Horizontal panning is now strictly clamped to the page bounds, including
  touch inertia, so the canvas cannot be pushed completely off screen.
- The temporary grid is drawn immediately on every visible notebook page,
  including adjacent pages during continuous scrolling.
- Page changes show the preloaded page surface immediately while the native
  vector surface refines, reducing visible page-loading pauses.
- Native notebooks now persist a small, sharp first-page cover and refresh it
  after first-page edits, avoiding repeated large-page deserialization in the
  home library. New paged notebooks start with Page 1 so a cover always exists.
- Folder cards now include a three-dot menu for uploading, replacing, or
  removing a custom thumbnail photo while retaining the folder icon by default.

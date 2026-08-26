# HoomNote 0.8.2

- Copy, cut, and paste no longer crash when Windows clipboard access fails. HoomNote keeps an in-process object or text copy as a fallback and reports paste failures in the app.
- Images now use their decoded EXIF orientation and preserve their aspect ratio on the canvas and in page thumbnails, fixing stretched and mismatched-looking image boxes.
- Deleting a notebook now waits for the SQLite transaction to finish before clearing tabs and page state. A failed delete leaves the open notebook intact, and post-delete asset cleanup failures no longer crash the app.
- Precision touchpads and horizontal mouse wheels can now pan left and right with momentum without triggering vertical page navigation.

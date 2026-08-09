# HoomNote 0.8.0

- Saving is safer and more responsive: HoomNote snapshots the document before writing, saves only changed pages when possible, and renames notebooks without rewriting their page content.
- The notebook database now uses explicit schema migrations, verifies its integrity at startup, keeps rotating recovery backups, and automatically restores or quarantines a damaged database without discarding the original files.
- Imported images and PDF layers are stored safely under concurrent imports, unused assets are reclaimed after notebook deletion, and assets needed by retained recovery backups remain protected.
- HoomNote package imports now assign fresh IDs to the notebook and every nested page, object, group, section, and stroke relationship, preventing a second import from replacing the first. Export also refuses incomplete, partially loaded notebooks instead of silently dropping pages.
- PDF, Office, and Samsung Notes imports use less memory, cancel and clean up reliably, and avoid retaining unnecessary conversion files or source archives.
- Large handwritten notebooks use substantially less memory through compact ink-point storage, cached stroke bounds, streamed database loading, adaptive preview budgets, and bounded shared PDF caches.
- Lasso selection, stroke hit testing, erasing, and spatial indexing are faster on dense pages and remain correct for scaled, rotated, or very large content. Invalid coordinates are ignored safely, and equal-layer objects retain stable authored order.
- Undo and redo immediately correct only the most recent command's affected region; recently drawn strokes no longer flash or replay while the retained page frame catches up.
- Long segment-eraser scrubs avoid per-segment temporary allocations, keeping dense handwritten pages responsive during sustained erasing.
- SVG export now streams directly to disk, including large embedded images, instead of constructing the entire document in memory.
- Background diagnostics are bounded and asynchronous, so logging and log rotation no longer perform disk I/O on drawing or interaction paths.
- Oversized pages can render below one-sixteenth scale to stay within graphics-device limits while normal-sized pages retain native-resolution detail.
- Writing and erasing continue to preserve sharp committed page tiles and correction overlays without flashing the page background.

# HoomNote 0.8.1

- Writing no longer pauses at a fixed stroke interval. Completed strokes are composited incrementally into the retained page and only the detail tiles they intersect, avoiding periodic full overlay compaction work.
- Rapid back-to-back strokes remain visible while the renderer catches up, and partially drained render queues can no longer be mistaken for a fully current page frame.
- Erasing preserves the previous committed correction until its replacement frame is ready, preventing erased content from flashing or briefly returning when a new erase gesture begins or is cancelled.

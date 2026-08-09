# HoomNote 0.7.49

- Moving ink, shapes, images, and multi-object selections now keeps the fast GPU move cache active through pointer release and the committed-page handoff, avoiding an expensive UI-thread redraw at the end of a drag.
- Retained, pending, and standby page textures are now matched against the exact page revision, preventing a pre-move texture from being accepted as current and briefly restoring the original object.
- A valid selection preview is now committed when Windows cancels a captured pointer, preventing completed moves from snapping back or being silently discarded on affected input devices.

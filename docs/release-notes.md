# HoomNote 0.7.33

- Made the home library responsive with dense notebooks by skipping oversized
  cover previews, limiting background preview work, and avoiding large
  thumbnail-related memory spikes.
- Made notebook switching faster and interruptible. Obsolete loads are now
  cancelled, large page data is parsed away from the UI thread, and the two
  most recently opened notebooks stay ready for quick switching.
- Reduced page-switch buffering by prioritizing the selected page thumbnail
  and delaying smaller adjacent-page previews until navigation settles.

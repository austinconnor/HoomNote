# HoomNote 0.7.3

- Fixed the startup crash caused by quick ink controls firing before the page finished constructing.
- Fixed published builds stripping the WinRT collection metadata required by notebook and page lists.
- Restored reflection-based persistence metadata so the local library loads correctly in release builds.
- Added precise constructor lifecycle diagnostics for future startup failures.

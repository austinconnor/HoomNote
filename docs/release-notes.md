# HoomNote 0.7.2

- Fixes a startup crash caused by synchronous Windows shell icon refresh.
- Moves installed-app icon metadata maintenance off the critical UI startup
  path and removes the unsafe shell-wide native notification.

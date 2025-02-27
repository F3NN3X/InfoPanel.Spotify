# Changelog

All notable changes to the SpotifyPlugin for InfoPanel are documented here.

## [1.0.46] - 2025-02-27
- **Fixed**: .ini file locking on plugin reload causing freezes by replacing `ReadFile` with `FileStream` and `StreamReader` using `FileShare.Read`.
- **Removed**: Unnecessary `parser.Dispose()` calls.
- **Fixed**: `CS8604` warning by adding null suppression (`!`) to `_configFilePath` in `FileStream` calls.

## [1.0.45] - 2025-02-27
- **Changed**: Reverted `SetDefaultValues` and `HandleError` to synchronous methods since no async UI operations exist.
- **Removed**: Unused `_fileLock` static field.
- **Added**: Debug log in `SetDefaultValues` when message is "Unknown" to aid error tracking.

## [1.0.44] - 2025-02-27
- **Added**: Async-ready `SetDefaultValues` and `HandleError` with placeholder `Task.CompletedTask`.
- **Improved**: Capped `ExecuteWithRetry` delay at 10s for rate limit retries.

## [1.0.43] - 2025-02-27
- **Improved**: Pause detection with `_pauseCount` and `IsPlaying` flag.
- **Added**: Cached display strings (`_displayTrackName`, etc.) and `CutString` method.
- **Optimized**: Early exit in `GetSpotifyInfo` when no track is playing.
- **Refined**: `ExecuteWithRetry` with faster retries for non-rate-limit errors.
- **Enhanced**: `RateLimiter` with per-second limit (10 req/s).

## [1.0.42] - 2025-02-27
- **Added**: Album name display via `PluginText _album`.

## [1.0.40] - 2025-02-21
- **Added**: Configurable `MaxDisplayLength` in .ini (default 20), truncates titles with "...".

## [1.0.23] - 2025-02-20
- **Changed**: `_coverUrl` ID to "cover-art" for image recognition, using raw Spotify URL.

## [1.0.22] - 2025-02-20
- **Changed**: Appended .jpg to `_coverUrl` for image display.

## [1.0.21] - 2025-02-20
- **Changed**: Commented out `_coverArt` code, kept `_coverUrl` with raw URL.

## [1.0.20] - 2025-02-20
- **Changed**: Reverted `_coverIconUrl` to `_coverUrl`, restored `_coverArt` code.

## [1.0.19] - 2025-02-20
- **Changed**: Renamed `_coverUrl` to `_coverIconUrl` for compatibility.

## [1.0.18] - 2025-02-20
- **Changed**: Commented out `_coverArt`-related code.

## [1.0.17] - 2025-02-20
- **Fixed**: Cover URL display and track change update.

## [1.0.16] - 2025-02-20
- **Fixed**: Cover URL update on track change with dynamic construction.

## [1.0.15] - 2025-02-20
- **Fixed**: Ensured `_coverUrl.Value` gets raw Spotify URL.

## [1.0.14] - 2025-02-20
- **Added**: `PluginText` for cover art URL (`_coverUrl`).

## [1.0.13] - 2025-02-20
- **Added**: Track progression percentage via `PluginSensor _trackProgress` (0-100%, float).
- **Fixed**: Changed `_trackProgress.Value` to float.

## [1.0.12] - 2025-02-20
- **Fixed**: Dynamic total track time update.

## [1.0.11] - 2025-02-20
- **Added**: `PluginSensor` for total track time (`_totalTrackTime`).

## [1.0.10] - 2025-02-20
- **Added**: Resume animation ("Resuming..." for 1s) and track end refinement ("Track Ended" for 3s).

## [1.0.9] - 2025-02-20
- **Added**: Visual pause indication ("Paused" for track and artist).

## [1.0.8] - 2025-02-20
- **Fixed**: Pause detection timing with wider `ProgressToleranceMs` (1500ms).

## [1.0.7] - 2025-02-20
- **Fixed**: Reliable pause freeze with `_pauseDetected` flag.

## [1.0.6] - 2025-02-20
- **Fixed**: Simplified pause detection, forced sync on stall.

## [1.0.5] - 2025-02-20
- **Fixed**: Adjusted pause detection order.

## [1.0.4] - 2025-02-20
- **Added**: Pause detection between syncs.

## [1.0.3] - 2025-02-20
- **Improved**: Sync interval to 2s, forced sync on track end.

## [1.0.2] - 2025-02-20
- **Optimized**: Reduced API calls to 5s intervals, added cover art caching.

## [1.0.1] - 2025-02-20
- **Release**: Beta release with core functionality.

## [1.0.0] - 2025-02-20
- **Release**: Internal pre-release.
# Changelog

All notable changes to the SpotifyPlugin for InfoPanel are documented here.

# Changelog

## v1.0.60 (Mar 1, 2025)
- **Fixed**: Long-term restart token refresh.
  - **Changes**: Moved expiration check into `TryInitializeClientWithAccessToken()`, ensured refresh on failure in `Initialize()`.
  - **Purpose**: Resolves "The access token expired" after app closure >1 hour by properly refreshing on restart.

## v1.0.59 (Mar 1, 2025)
- **Fixed**: Token expiration handling on restart.
  - **Changes**: Adjusted `TryInitializeClientWithAccessToken()` to return `bool`, moved expiration logic inside, updated `Initialize()` to refresh on failure.
  - **Purpose**: Attempt to fix "The access token expired" after long app closure (incomplete fix, refined in v1.0.60).

## v1.0.58 (Mar 1, 2025)
- **Fixed**: Simplified token management for restart.
  - **Changes**: Removed `ExpiresIn`, used `WithToken()` for valid tokens, refreshed if expired, aligned with `SpotifyAPI.Web` best practices.
  - **Purpose**: Resolved "Only valid bearer authentication supported" by simplifying PKCE token handling; short restarts worked, long restarts still failed.

## v1.0.57 (Mar 1, 2025)
- **Fixed**: PKCE token restoration on restart.
  - **Changes**: Added `expires_in` to `.tmp`, aligned `TryInitializeClientWithAccessToken()` with Spotify PKCE flow by preserving exact token state.
  - **Purpose**: Attempt to fix "Only valid bearer authentication supported" (still failed due to token state mismatch).

## v1.0.56 (Mar 1, 2025)
- **Fixed**: Token restoration on restart.
  - **Changes**: Adjusted `TryInitializeClientWithAccessToken()` to use correct `ExpiresIn`, fixed null reference warning (CS8601).
  - **Purpose**: Attempt to resolve "Only valid bearer authentication supported" (incomplete due to static `ExpiresIn`).

## v1.0.55 (Mar 1, 2025)
- **Fixed**: Token reuse on restart.
  - **Changes**: Updated `TryInitializeClientWithAccessToken()` to include `refreshToken` in `PKCEAuthenticator` setup, ensuring valid token state.
  - **Purpose**: Attempt to fix "String is empty or null (Parameter 'refreshToken')" (failed due to `PKCETokenResponse` issues).

## v1.0.54 (Mar 1, 2025)
- **Fixed**: Invalid bearer token on restart.
  - **Changes**: Simplified `TryInitializeClientWithAccessToken()` to avoid recalculating `ExpiresIn`, ensuring valid token reuse from `.tmp`.
  - **Purpose**: Attempt to resolve "Only valid bearer authentication supported" (still failed due to token state).

## v1.0.53 (Mar 1, 2025)
- **Fixed**: Token loading on restart with enhanced logging.
  - **Changes**: Improved `.tmp` parsing with validation, added detailed logging to debug token issues.
  - **Purpose**: Diagnose "Error updating Spotify info" on restart (identified bearer token issue).

## [1.0.52] - 2025-02-28
- **Improved**: Fine-tuned background token refresh.
  - Changes: Increased `TokenRefreshCheckIntervalSeconds` to 900s (15min), added retry mechanism with 3 attempts in `StartBackgroundTokenRefresh()`.
  - Purpose: Reduce check frequency and improve refresh reliability.

## [1.0.51] - 2025-02-28
- **Added**: Background access token refresh.
  - Changes: Implemented automatic refresh in a background task without user interaction.
  - Purpose: Keep access token valid seamlessly.

## [1.0.50] - 2025-02-28
- **Renamed**: Changed `APIKey` to `ClientID`.
  - Changes: Updated all references from `APIKey` to `ClientID` for consistency with Spotify terminology.
  - Purpose: Improve clarity and alignment with Spotify API naming.

## [1.0.49] - 2025-02-28
- **Fixed**: Token reading from `spotifyrefresh.tmp`.
  - Changes: Ensured tokens are always read from `.tmp`, improved token file creation and debugging.
  - Purpose: Resolve "Error updating Spotify info" when `RefreshToken` is removed from `.ini`.

## [1.0.48] - 2025-02-28
- **Changed**: Moved token storage to separate file.
  - Changes: Relocated `RefreshToken`, `AccessToken`, and `TokenExpiration` to `spotifyrefresh.tmp`, keeping `.ini` for `APIKey` and `MaxDisplayLength`.
  - Purpose: Simplify user configuration and separate state from settings.

## [1.0.47] - 2025-02-28
- **Optimized**: Refresh token handling.
  - Changes: Store access token and expiration in `.ini`, only refresh if expired or invalid.
  - Purpose: Reduce unnecessary token refreshes on plugin reload.

## [1.0.46] - 2025-02-27
- **Fixed**: `.ini` file locking on plugin reload causing freezes by replacing `ReadFile` with `FileStream` and `StreamReader` using `FileShare.Read`.
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
- **Added**: Configurable `MaxDisplayLength` in `.ini` (default 20), truncates titles with "...".

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
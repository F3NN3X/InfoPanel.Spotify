# Changelog

All notable changes to the SpotifyPlugin for InfoPanel are documented here.

## [1.2.0] - September 19, 2025
### Added
- **Keep song information when paused**: Track name, artist, and album are now preserved when playback is paused instead of showing "Paused"
- **Custom messages via INI configuration**: Added two new configuration options for customizing display messages:
  - `NoTrackMessage`: Custom message when no track is loaded in Spotify (default: "No music playing")
  - `PausedMessage`: Custom message when track is paused (empty = keep track info, or set custom message like "⏸ Paused")

### Changed
- Enhanced `PlaybackInfo` record with `HasTrack` field to distinguish between paused tracks vs. no track loaded
- Updated `SpotifyPlaybackService` to preserve actual track information during pause states
- Modified UI update logic in `OnPlaybackUpdated()` to handle different playback states appropriately
- Improved configuration file handling to include new message settings with backward compatibility

### Technical Details
- Added `_lastKnownTrack` tracking in `SpotifyPlaybackService` to maintain track information across state changes
- Updated pause detection logic to preserve track metadata when `IsPlaying` is false but `HasTrack` is true
- Enhanced INI file loading with automatic addition of missing configuration keys
- Maintained backward compatibility - existing installations will get default values automatically

### Configuration Example
```ini
[Spotify Plugin]
ClientID=your-client-id-here
MaxDisplayLength=20
NoTrackMessage=♪ Spotify idle
PausedMessage=
ForceInvalidGrant=false
```

## [1.1.1] - June 11, 2025
### Fixed
- Resolved code issues in sealed classes:
  - Removed `virtual` keyword from methods in sealed classes (not allowed in C#)
  - Changed `protected` methods to `private` in sealed classes since they can't be inherited
- Affected files:
  - `Services/SpotifyPlaybackService.cs`: Updated event methods `OnPlaybackUpdated` and `OnPlaybackError`
  - `Services/SpotifyAuthService.cs`: Updated event methods `OnAuthStateChanged` and `OnClientInitialized`
### Benefits
- Code now compiles without errors or warnings
- Improved architectural consistency with C# best practices
- Better adherence to sealed class semantics

## [1.1.0] - June 11, 2025
### Changed
- Completely refactored the codebase to improve maintainability by splitting classes into separate files:
  - Created `Models/AuthState.cs` to hold the authentication state enum
  - Created `Services/RateLimiter.cs` for the rate limiting functionality
  - Created `Services/SpotifyAuthService.cs` to handle authentication and token management
  - Created `Services/SpotifyPlaybackService.cs` to manage playback information and updates
  - Reduced the size of the main `InfoPanel.Spotify.cs` file by delegating responsibilities to specialized classes
### Benefits
- Improved code organization and readability with single-responsibility classes
- Better maintainability with isolated components
- Easier navigation and troubleshooting
- Enhanced collaboration potential with clearly defined class boundaries
- More effective version control with smaller, focused files

# Changelog

## [1.0.91] - June 10 2025
### Changed
- Changed callback url from localhost to 127.0.0.1 to comply with new Spotify guidelines: https://developer.spotify.com/documentation/web-api/concepts/redirect_uri

## [1.0.90] - 2025-03-02
### Changed
- Reduced `TokenRefreshCheckIntervalSeconds` from 1500s (~25m) to 60s (~1m) in `StartBackgroundTokenRefresh` to ensure timely token refresh before expiry.
- Fixed `_trackProgress.Value` type mismatch in `GetSpotifyInfo` from string `"No track playing"` to float `0.0F`, resolving compile error CS0029.
### Purpose
- Ensures background refresh triggers reliably within the 60s buffer (`TokenExpirationBufferSeconds`), preventing token expiry failures during long runs (e.g., >2h tests with multiple refreshes).
- Corrects type error for `PluginSensor` value, ensuring proper compilation and functionality.

## [1.0.88] - 2025-03-02
### Changed
- Simplified `SetDefaultValues` and `HandleError` methods by removing redundant `"Unknown"` fallback logic in `SetDefaultValues`.
- Removed unused `using` statements (e.g., `System.ComponentModel`, `System.Web`).
### Purpose
- Reduces code redundancy and improves readability without altering functionality—pure style cleanup.

## [1.0.87] - 2025-03-02
### Changed
- Fixed redundant `TryInitializeClientWithAccessToken` calls in `Initialize` by storing result in a `bool` variable, reducing log noise (e.g., duplicate "Initialized Spotify client" messages).
- Updated `ExecuteWithRetry` to throw `AggregateException` with retry context instead of bare `lastException`.
- Restricted `_forceInvalidGrant` loading from `.ini` to debug builds using `#if DEBUG`, forcing `false` in release builds.
### Purpose
- Cleaner initialization logs, better error context for retries, and production safety by disabling debug flags in release.

## [1.0.86] - 2025-03-02
### Changed
- Enhanced refresh logging in `StartBackgroundTokenRefresh` and `TryRefreshTokenAsync` for clearer timing diagnostics (e.g., "Background token refresh triggered at UTC...").
- Ensured `SaveTokens` consistently updates `.tmp` file during refreshes, fixing occasional non-updates.
- Adjusted `forceInvalidGrant` timing to align with refresh simulation in debug mode.
### Purpose
- Improved debug visibility into refresh cycles, fixed `.tmp` persistence issues (e.g., token expiry not updating), and refined debug simulation behavior.

## [1.0.85] - 2025-03-02
### Changed
- Silenced nullable warning CS8625 in `TryRefreshTokenAsync` by adding `null!` to `APIException` throw for `forceInvalidGrant`.
### Purpose
- Clean compile with no warnings—cosmetic fix, no functional change.

## v1.0.70 (Mar 1, 2025)
- **Optimized**: Background refresh interval.
  - **Changes**: Increased `TokenRefreshCheckIntervalSeconds` from 30s to 1740s (~29 minutes) for ~2 checks/hour.
  - **Purpose**: Reduce excessive polling while ensuring refresh before 1-hour token expiry with 60s buffer.

## v1.0.69 (Mar 1, 2025)
- **Fixed**: Background refresh startup reliability.
  - **Changes**: Replaced blocking `.GetAwaiter().GetResult()` in `Initialize()` with async `Task.Run()` for refresh, enhanced logging to track task execution, ensured `_authState` syncs on all failures.
  - **Purpose**: Prevent deadlocks, confirm background task runs, and align auth state with refresh success/failure.

## v1.0.68 (Mar 1, 2025)
- **Fixed**: Background token refresh reliability.
  - **Changes**: Reduced `TokenRefreshCheckIntervalSeconds` to 30s, forced initial refresh in `Initialize()`, enhanced state sync and logging, avoided blocking in refresh startup.
  - **Purpose**: Ensure token refreshes catch expiry early, run reliably, and log clearly for debugging.

## v1.0.67 (Mar 1, 2025)
- **Fixed**: Background token refresh timing.
  - **Changes**: Reduced `TokenRefreshCheckIntervalSeconds` to 60s, synced `_authState` on refresh failure, improved refresh logging.
  - **Purpose**: Ensure timely token refresh within 1-hour expiry, reflect auth state accurately, and debug refresh issues.

## v1.0.66 (Mar 1, 2025)
- **Fixed**: Nullable warning in background refresh.
  - **Changes**: Made `_refreshCancellationTokenSource` non-nullable (CS8602), adjusted `Close()` for safety.
  - **Purpose**: Eliminate compile warning while preserving functionality.

## v1.0.65 (Mar 1, 2025)
- **Enhanced**: Authentication flow and UI clarity.
  - **Changes**: Renamed button to "Authorize with Spotify", added text mappings for auth state sensor in logs/README (0=Not Authenticated, 1=Authenticating, 2=Authenticated, 3=Error), restored background token refresh on startup for expired tokens while retaining button for initial/manual auth.
  - **Purpose**: Improve button UX, clarify auth state, and ensure seamless long-term restarts with expired tokens.

## v1.0.64 (Mar 1, 2025)
- **Enhanced**: Robustness and debugging improvements.
  - **Changes**: Reduced exception noise in `ExecuteWithRetry()`, made `Initialize()` and `Close()` reentrant, added `PluginSensor` for auth state.
  - **Purpose**: Improve stability for reloads, reduce log clutter, and aid debugging with visible auth status.

## v1.0.63 (Mar 1, 2025)
- **Added**: "Start Spotify Auth" button.
  - **Changes**: Moved `StartAuthentication()` from auto-trigger in `Initialize()` to manual `StartSpotifyAuth()` with `[PluginAction]`, kept background refresh and v1.0.62 refinements.
  - **Purpose**: Enable user-initiated Spotify authentication instead of automatic startup.

## v1.0.62 (Mar 1, 2025)
- **Refined**: Fixed compile errors from v1.0.61 refinements.
  - **Changes**: Reverted `GetContainers()` to `Load()` for `BasePlugin` compliance (CS0115, CS0534), moved container setup back to `Load()`, kept `Update()` with sync-async comment, retained `_trackEndTime` with note.
  - **Purpose**: Ensure `BasePlugin` interface adherence while preserving v1.0.61 enhancements.

## v1.0.61 (Mar 1, 2025)
- **Refined**: Code improvements post-v1.0.60.
  - **Changes**: Added `_spotifyClient` null check in `StartBackgroundTokenRefresh`, removed redundant `Load()` (re-added as empty override), synced `SyncIntervalSeconds` with `UpdateInterval`, improved resuming logic with state flag and clearer text ("Resuming Playback..."), fixed compile errors (CS0534, CS0102, CS0229).
  - **Purpose**: Enhance robustness, remove dead code, improve resuming display reliability, and ensure `BasePlugin` compliance.

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

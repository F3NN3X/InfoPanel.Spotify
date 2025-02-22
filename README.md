# Spotify Info - SpotifyPlugin

- **Plugin**: Spotify Info - SpotifyPlugin
- **Version**: 1.0.23
- **Description**: A plugin for InfoPanel to display current Spotify track information, including track name, artist, cover URL, elapsed time, and remaining time. Utilizes the Spotify Web API with PKCE authentication and updates every 1 second for UI responsiveness, with optimized API calls to minimize overhead. Supports `PluginSensor` for track progression and `PluginText` for cover URL display.

## Changelog

- **v1.0.23 (Feb 20, 2025)**: Changed `_coverUrl` ID to `"cover-art"` for image recognition.
  - **Changes**: Renamed `_coverUrl`'s ID to `"cover-art"` to align with the original `_coverArt` naming, using the raw Spotify URL. Kept `_coverArt` code commented out for reference.
  - **Purpose**: Signals InfoPanel to render `_coverUrl` as an image without requiring changes to the core InfoPanel codebase.

- **v1.0.22 (Feb 20, 2025)**: Appended `.jpg` to `_coverUrl` for image display.
  - **Changes**: Added `.jpg` extension to the raw Spotify URL in `_coverUrl.Value` to ensure proper image rendering.

- **v1.0.21 (Feb 20, 2025)**: Commented out `_coverArt` code, retained `_coverUrl` with raw URL.
  - **Changes**: Re-commented all `_coverArt`-related code to disable it, ensured `_coverUrl` uses the raw Spotify URL without modifications.

- **v1.0.20 (Feb 20, 2025)**: Reverted `_coverIconUrl` to `_coverUrl` and restored `_coverArt` code.
  - **Changes**: Renamed the field back to `_coverUrl`, set it to the raw Spotify URL, and uncommented `_coverArt` code for potential reuse.

- **v1.0.19 (Feb 20, 2025)**: Renamed `_coverUrl` to `_coverIconUrl` for image display compatibility.
  - **Changes**: Adjusted the field ID to `"cover_icon_url"` to match the naming convention used in WeatherPlugin for image handling.

- **v1.0.18 (Feb 20, 2025)**: Commented out `_coverArt`-related code.
  - **Changes**: Removed local cover art downloading and caching logic, shifted focus to using `_coverUrl` exclusively.

- **v1.0.17 (Feb 20, 2025)**: Fixed cover URL display and track change update.
  - **Fixes**: Reverted to using the raw `coverArtUrl` for `_coverUrl.Value`, ensured updates occur reliably on track changes.

- **v1.0.16 (Feb 20, 2025)**: Fixed cover URL update on track change with dynamic construction.
  - **Fixes**: Implemented dynamic URL construction (`https://i.scdn.co/image/{imageId}`) to ensure the cover URL updates correctly.

- **v1.0.15 (Feb 20, 2025)**: Fixed cover URL display.
  - **Fixes**: Ensured `_coverUrl.Value` consistently receives the raw Spotify URL for proper display.

- **v1.0.14 (Feb 20, 2025)**: Added `PluginText` for cover art URL.
  - **Features**: Introduced `_coverUrl` to store and display the raw Spotify cover art URL in the UI.

- **v1.0.13 (Feb 20, 2025)**: Replaced total track time with track progression percentage (fixed float compatibility).
  - **Features**: Added `_trackProgress` as a `PluginSensor` (0-100%, float) to show dynamic playback progress.
  - **Fixes**: Changed `_trackProgress.Value` from `double` to `float` to match `PluginSensor` requirements.

- **v1.0.12 (Feb 20, 2025)**: Fixed dynamic total track time update.
  - **Fixes**: Ensured `_totalTrackTime` updates every 1 second to reflect the static track duration.

- **v1.0.11 (Feb 20, 2025)**: Added `PluginSensor` for total track time.
  - **Features**: Introduced `_totalTrackTime` to display the track duration in milliseconds.

- **v1.0.10 (Feb 20, 2025)**: Added resume animation and track end refinement.
  - **Features**: Displays `"Resuming..."` for 1 second during resume; shows `"Track Ended"` for 3 seconds at track completion.

- **v1.0.9 (Feb 20, 2025)**: Added visual pause indication.
  - **Features**: Sets track name and artist to `"Paused"` when playback is paused.

- **v1.0.8 (Feb 20, 2025)**: Fixed pause detection timing.
  - **Fixes**: Increased `ProgressToleranceMs` to 1500ms for more reliable pause detection; moved pause check to align with API sync.

- **v1.0.7 (Feb 20, 2025)**: Reliable pause freeze attempt.
  - **Fixes**: Implemented local pause detection using a `_pauseDetected` flag to improve reliability.

- **v1.0.6 (Feb 20, 2025)**: Robust pause detection attempt.
  - **Fixes**: Simplified pause detection logic and forced API sync when a stall is detected.

- **v1.0.5 (Feb 20, 2025)**: Adjusted pause detection.
  - **Fixes**: Moved pause check to execute before progress estimation for better accuracy.

- **v1.0.4 (Feb 20, 2025)**: Pause detection enhancement.
  - **Features**: Added pause detection between API syncs to catch pauses more effectively.

- **v1.0.3 (Feb 20, 2025)**: Responsiveness improvement.
  - **Features**: Reduced sync interval to 2 seconds; forced sync when a track ends for immediate updates.

- **v1.0.2 (Feb 20, 2025)**: Performance optimization.
  - **Features**: Implemented caching to reduce API calls to every 5 seconds; added cover art caching for efficiency.

- **v1.0.1**: Beta release with core functionality.
- **v1.0.0**: Internal pre-release.

## Notes

- Spotify API rate limits are estimated at approximately 180 requests per minute. For more details, see the [Spotify Web API Rate Limits documentation](https://developer.spotify.com/documentation/web-api/concepts/rate-limits).

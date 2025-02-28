# SpotifyPlugin for InfoPanel

A plugin for InfoPanel to display real-time Spotify track information, including track name, artist, album, cover URL, elapsed time, and remaining time.

## Features
- Displays current track details: title, artist, album, cover art URL, elapsed/remaining time, and progress percentage.
- Configurable title truncation via `MaxDisplayLength` in `.ini` file (default: 20 characters).
- Robust pause, resume, and track end detection with visual indicators.
- Optimized API usage with caching, rate limiting (180 req/min, 10 req/s), and automatic background token refresh.
- PKCE authentication with Spotify Web API for secure token management.

## Installation and Setup
Follow these steps to get the SpotifyPlugin working with InfoPanel:

1. **Download the Plugin**:
   - Download the latest release ZIP file (`SpotifyPlugin-vX.X.X.zip`) from the [GitHub Releases page](https://github.com/F3NN3X/InfoPanel.Spotify/releases).

2. **Import the Plugin into InfoPanel**:
   - Open the InfoPanel app.
   - Navigate to the **Plugins** page.
   - Click **Import Plugin Archive**, then select the downloaded ZIP file.
   - InfoPanel will extract and install the plugin.

3. **Configure the Plugin**:
   - On the Plugins page, click **Open Plugins Folder** to locate the plugin files.
   - Close InfoPanel.
   - Open `InfoPanel.Spotify.dll.ini` in a text editor (e.g., Notepad).
   - Replace `<your-spotify-client-id>` with your Spotify Client ID (see "Obtaining a Spotify Client ID" below).
   - Save and close the file.

4. **Authorize with Spotify**:
   - Restart InfoPanel.
   - The plugin will open a browser window prompting you to log in to Spotify and authorize the app.
   - After authorization, the plugin will save tokens to `spotifyrefresh.tmp` and start working.

5. **Enjoy**:
   - Play music in Spotify, and the plugin will display track details in InfoPanel automatically.

## Obtaining a Spotify Client ID
1. Go to the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard/).
2. Log in with your Spotify account.
3. Click **Create an App**:
   - Enter a name (e.g., "InfoPanel Spotify Plugin") and description.
   - Set the **Redirect URI** to `http://localhost:5000/callback`.
   - Accept the terms and click **Create**.
4. Copy the **Client ID** from the app’s dashboard.
5. Paste it into `InfoPanel.Spotify.dll.ini` as described in step 3 above.

### Troubleshooting Tips
1. **"Error updating Spotify info"**:
   - **Fixed in v1.0.60**: Short and long restarts (even after token expiration) should now work seamlessly by refreshing the token automatically.
   - **Fallback**: If issues persist (e.g., network failure during refresh), close InfoPanel, delete `spotifyrefresh.tmp` from the plugins folder (via **Open Plugins Folder**), and restart to reauthorize.

2. **No Browser Window for Authorization**:
   - Fix: Verify `ClientID` and redirect URI (`http://localhost:5000/callback`) in Spotify Dashboard.

3. **No Track Info After Authorization**:
   - Fix: Ensure Spotify is playing and check network.

## Requirements for compile
- .NET 8.0
- InfoPanel application
- Spotify API Client ID (set in `.ini` file)
- Dependencies: `SpotifyAPI.Web`, `IniParser` (bundled in release)


## Configuration
- **`InfoPanel.Spotify.dll.ini`**:
  ```ini
  [Spotify Plugin]
  ClientID=<your-spotify-client-id>
  MaxDisplayLength=20

# SpotifyPlugin for InfoPanel

A plugin for InfoPanel to display real-time Spotify track information, including track name, artist, album, cover URL, elapsed time, and remaining time. Features smart pause handling that preserves track information and customizable display messages.

## Features
- Displays current track details: title, artist, album, cover art URL, elapsed/remaining time, and progress percentage.
- **Preserves track information when paused**: Song name, artist, and album remain visible during pause (v1.2.0+).
- **Customizable display messages**: Configure custom messages for "no track playing" and paused states via `.ini` file (v1.2.0+).
- Configurable title truncation via `MaxDisplayLength` in `.ini` file (default: 20 characters).
- Robust pause, resume, and track end detection with visual indicators.
- Optimized API usage with caching, rate limiting (180 req/min, 10 req/s), and automatic background token refresh.
- PKCE authentication with Spotify Web API for secure token management.

## Installation and Setup
Follow these steps to get the SpotifyPlugin working with InfoPanel:

1. **Download the Plugin**:
   - Download the latest release ZIP file (`SpotifyPlugin-v1.2.0.zip` or newer) from the [GitHub Releases page](https://github.com/F3NN3X/InfoPanel.Spotify/releases).
   - **v1.2.0+** includes track preservation during pause and customizable messages.

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
   - Initiate or re-authenticate Spotify by clicking the "Authorize with Spotify" button in the InfoPanel UI (v1.0.63+). Required for first use or if automatic refresh fails.
   - After authorization, the plugin will save tokens to `spotifyrefresh.tmp` and start working.

5. **Enjoy**:
   - Play music in Spotify, and the plugin will display track details in InfoPanel automatically.

## Obtaining a Spotify Client ID
1. Go to the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard/).
2. Log in with your Spotify account.
3. Click **Create an App**:
   - Enter a name (e.g., "InfoPanel Spotify Plugin") and description.
   - Set the **Redirect URI** to `http://127.0.0.1:5000/callback`.
   - Accept the terms and click **Create**.
4. Copy the **Client ID** from the app’s dashboard.
5. Paste it into `InfoPanel.Spotify.dll.ini` as described in step 3 above.

## Troubleshooting Steps & Error Messages

If the plugin isn’t working as expected, check the InfoPanel UI for these error messages and follow the steps below to resolve them. These are the messages you’ll see in the **track**, **artist**, or **album** fields—keep an eye on the **"Current Track"** display for the main clue.

### **Error Message: "Spotify client not initialized"**

- **What It Means**: The plugin hasn’t connected to Spotify yet—either you haven’t authorized it, or the initial setup failed.
- **How to Fix**:
  - Click **"Authorize with Spotify"** in the InfoPanel plugin settings.
  - Follow the browser prompt to log in to your Spotify account and allow access.
  - Wait a few seconds—the track info should start showing if Spotify is playing.
- **If It Persists**: Ensure your Client ID is set correctly in `InfoPanel.Spotify.dll.ini` (default file has a placeholder `<your-spotify-client-id>`—replace it with your Spotify app’s Client ID from [developer.spotify.com](https://developer.spotify.com/dashboard)).

### **Error Message: "Reauthorize Required"**

- **What It Means**: The plugin’s refresh token is invalid (e.g., revoked by Spotify after inactivity or manual revocation), and it can’t update your track info.
- **How to Fix**:
  - Click **"Authorize with Spotify"** again in the plugin settings.
  - Log in and grant access in the browser prompt.
  - The plugin will generate a new token and resume normal operation.
- **If It Persists**: Delete the `spotifyrefresh.tmp` file in the plugin folder (e.g., `C:\ProgramData\InfoPanel\plugins\InfoPanel.Spotify\`), then reauthorize.

### **Error Message: "Error updating Spotify info"**

- **What It Means**: The plugin hit a snag fetching your track data—could be a network issue, rate limit, or token problem it couldn’t recover from.
- **How to Fix**:
  - Check your internet connection—ensure Spotify is reachable.
  - Wait 10-20 seconds; the plugin retries automatically every second.
  - If still stuck, restart InfoPanel and check if the track info updates.
  - If it doesn’t clear, click **"Authorize with Spotify"** to refresh the connection.
- **If It Persists**: Delete `spotifyrefresh.tmp` and reauthorize—might be a stale token or corrupted file.

### **Error Message: "Error refreshing token"**

- **What It Means**: The plugin tried to refresh your access token but failed—likely a network issue or Spotify rejecting the refresh token.
- **How to Fix**:
  - Verify your internet connection is stable.
  - Click **"Authorize with Spotify"** to start fresh with a new token.
- **If It Persists**: Delete `spotifyrefresh.tmp`, reauthorize, and ensure your Spotify app’s permissions haven’t been revoked at [spotify.com/account/apps](https://www.spotify.com/account/apps).

### **Error Message: "Rate limit exceeded"**

- **What It Means**: The plugin hit Spotify’s API rate limit (estimated ~180 requests/minute)—too many requests too fast.
- **How to Fix**:
  - Wait a minute—the plugin will retry automatically after a short delay.
  - If it keeps happening, reduce other Spotify API usage on your network (e.g., close other Spotify apps or plugins).
- **If It Persists**: Restart InfoPanel to reset the rate limiter—should be rare with normal use.

### **Error Message: "No track playing"**

- **What It Means**: Spotify isn’t playing anything right now, or the plugin can’t detect playback.
- **How to Fix**:
  - Open Spotify and start playing a track.
  - Wait 1-2 seconds—track info should appear.
- **If It Persists**: Ensure Spotify is running and your account is active—pause/unpause to nudge it.

### **Error Message: "Error saving tokens: [details]"**

- **What It Means**: The plugin couldn’t write to `spotifyrefresh.tmp`—might be a file permission or disk issue.
- **How to Fix**:
  - Check if the plugin folder (e.g., `C:\ProgramData\InfoPanel\plugins\InfoPanel.Spotify\`) is writable—right-click, **Properties**, **Security** tab.
  - Delete `spotifyrefresh.tmp` if it exists, then reauthorize via **"Authorize with Spotify"**.

### **Error Message: "Spotify ClientID is not set or is invalid"**

- **What It Means**: The `ClientID` in `InfoPanel.Spotify.dll.ini` is missing or wrong—setup didn’t complete.
- **How to Fix**:
  - Open `InfoPanel.Spotify.dll.ini` in the plugin folder.
  - Replace `<your-spotify-client-id>` under `[Spotify Plugin]` with your Spotify app’s Client ID (get it from [developer.spotify.com](https://developer.spotify.com/dashboard)).
  - Save, restart InfoPanel, and authorize again.
- **If It Persists**: Double-check the ID—copy-paste it exactly, no extra spaces.

### **General Tips**

- **Restart InfoPanel**: Fixes most temporary glitches—close and reopen the app.
- **Check Spotify**: Ensure it’s playing and your account is logged in—plugin mirrors what’s active.

### **Still Stuck?**

If none of these fix it, your token might be revoked or there’s a Spotify API hiccup. Delete `spotifyrefresh.tmp`, reauthorize, and try again. For persistent issues, reach out via GitHub Issues with the error message and what you’ve tried!

## Contributing

Found a bug or have a feature idea? Open an [issue](https://github.com/F3NN3X/InfoPanel.Spotify/issues) or submit a [pull request](https://github.com/F3NN3X/InfoPanel.Spotify/pulls) on the repository!

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
  NoTrackMessage=No music playing
  PausedMessage=
  NoTrackArtistMessage=-
  ForceInvalidGrant=false
  ```

### Configuration Options

- **`ClientID`** *(required)*: Your Spotify Developer App Client ID. Get this from the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard/).

- **`MaxDisplayLength`** *(optional, default: 20)*: Maximum number of characters to display for track names, artist names, and album names. Longer text will be truncated with "...".

- **`NoTrackMessage`** *(optional, default: "No music playing")*: Custom message displayed when no track is loaded in Spotify. 
  - Examples: `"♪ Spotify idle"`, `"Ready to play music"`, `""`

- **`PausedMessage`** *(optional, default: "")*: Custom message displayed when playback is paused.
  - **Empty (default)**: Keep showing actual track information when paused
  - **Custom text**: Show custom message instead (e.g., `"⏸ Paused"`, `"Music paused"`)

- **`NoTrackArtistMessage`** *(optional, default: "-")*: Custom message for the artist field when no track is playing or when using a custom paused message.
  - **Default "-"**: Shows a dash in artist field
  - **Custom text**: Show custom text in artist field (e.g., `"No artist"`, `""`, `"Idle"`)
  - **Empty**: Leave artist field blank (may display as "0" in some cases)

- **`ForceInvalidGrant`** *(debug only)*: Available only in debug builds for testing token refresh functionality.

### Configuration Examples

**Default behavior** (keep track info when paused):
```ini
[Spotify Plugin]
ClientID=abc123xyz
MaxDisplayLength=25
NoTrackMessage=No music playing
PausedMessage=
NoTrackArtistMessage=-
```

**Custom messages**:
```ini
[Spotify Plugin] 
ClientID=abc123xyz
MaxDisplayLength=30
NoTrackMessage=♪ Spotify idle
PausedMessage=⏸ Music paused
NoTrackArtistMessage=No artist
```

**Minimal setup**:
```ini
[Spotify Plugin]
ClientID=abc123xyz
```

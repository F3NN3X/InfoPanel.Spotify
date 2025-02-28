using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using InfoPanel.Plugins;
using IniParser;
using IniParser.Model;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;

/*
 * Plugin: Spotify Info - SpotifyPlugin
 * Version: 1.0.52
 * Description: A plugin for InfoPanel to display current Spotify track information, including track name, artist, album, cover URL, elapsed time, and remaining time. Uses the Spotify Web API with PKCE authentication and updates every 1 second for UI responsiveness, with optimized API calls. Supports PluginSensor for track progression and PluginText for cover URL.
 * Changelog:
 *   - v1.0.52 (Feb 28, 2025): Fine-tuned background token refresh.
 *     - Changes: Increased TokenRefreshCheckIntervalSeconds to 900s (15min), added retry mechanism with 3 attempts in StartBackgroundTokenRefresh().
 *     - Purpose: Reduce check frequency and improve refresh reliability.
 *   - For full history, see CHANGELOG.md.
 * Note: Spotify API rate limits estimated at ~180 requests/minute (https://developer.spotify.com/documentation/web-api/concepts/rate-limits).
 */

namespace InfoPanel.Spotify
{
    // SpotifyPlugin class: Integrates Spotify playback info into InfoPanel
    public class SpotifyPlugin : BasePlugin
    {
        // UI display elements (PluginText) for InfoPanel
        private readonly PluginText _currentTrack = new("current-track", "Current Track", "-"); // Track name display
        private readonly PluginText _artist = new("artist", "Artist", "-"); // Artist name display
        private readonly PluginText _album = new("album", "Album", "-"); // Album name display
        private readonly PluginText _elapsedTime = new("elapsed-time", "Elapsed Time", "00:00"); // Elapsed time display
        private readonly PluginText _remainingTime = new("remaining-time", "Remaining Time", "00:00"); // Remaining time display
        private readonly PluginText _coverUrl = new("cover-art", "Cover URL", ""); // Cover art URL display

        // UI display elements (PluginSensor) for InfoPanel
        private readonly PluginSensor _trackProgress = new("track-progress", "Track Progress (%)", 0.0F); // Progress percentage (0-100%)

        // Spotify API and authentication fields
        private SpotifyClient? _spotifyClient; // Client for Spotify API calls
        private string? _verifier; // PKCE verifier for authentication
        private EmbedIOAuthServer? _server; // Local server for OAuth callback
        private string? _clientID; // Spotify API client ID
        private string? _configFilePath; // Path to .ini config file
        private string? _tokenFilePath; // Path to token storage file
        private string? _refreshToken; // Refresh token for API access
        private string? _accessToken; // Access token for API calls
        private DateTime _tokenExpiration; // Expiration time of the access token

        // Background refresh task
        private CancellationTokenSource _refreshCancellationTokenSource; // Token to cancel background refresh task

        // Rate limiter to manage API request frequency
        private readonly RateLimiter _rateLimiter = new RateLimiter(180, TimeSpan.FromMinutes(1), 10, TimeSpan.FromSeconds(1));

        // Cache for playback state to reduce API calls
        private string? _lastTrackId; // ID of the last synced track
        private int _lastProgressMs; // Last known progress in milliseconds
        private int _previousProgressMs; // Progress from the prior sync
        private int _lastDurationMs; // Last known track duration in milliseconds
        private bool _isPlaying; // Indicates if a track is currently playing
        private DateTime _lastApiCallTime = DateTime.MinValue; // Timestamp of the last API call
        private bool _pauseDetected; // Flag to indicate a detected pause
        private int _pauseCount; // Counter for consecutive pause detections
        private bool _trackEnded; // Flag for track end state
        private DateTime _trackEndTime; // Time when the track ended
        private bool _isResuming; // Flag for resume animation
        private DateTime _resumeStartTime; // Time when resume started
        private string? _lastTrackName; // Cached last track name
        private string? _lastArtistName; // Cached last artist name
        private string? _lastAlbumName; // Cached last album name

        // Cached display strings for optimized UI updates
        private string? _displayTrackName; // Truncated track name for display
        private string? _displayArtistName; // Truncated artist name for display
        private string? _displayAlbumName; // Truncated album name for display

        // Configurable settings
        private int _maxDisplayLength = 20; // Max characters for truncation, set via .ini

        // Constants for timing and detection thresholds
        private const int SyncIntervalSeconds = 1; // API sync interval
        private const int ProgressToleranceMs = 1500; // Tolerance for pause detection (ms)
        private const int PauseThreshold = 2; // Consecutive stalls needed to confirm pause
        private const int TokenExpirationBufferSeconds = 60; // Buffer before token expiration to trigger refresh
        private const int TokenRefreshCheckIntervalSeconds = 900; // Check token every 15 minutes
        private const int TokenRefreshMaxRetries = 3; // Max retry attempts for background refresh
        private const int TokenRefreshRetryDelaySeconds = 5; // Initial delay between retries

        // Constructor: Initializes the plugin with metadata
        public SpotifyPlugin()
            : base("spotify-plugin", "Spotify", "Displays the current Spotify track information. Version: 1.0.52")
        {
            _refreshCancellationTokenSource = new CancellationTokenSource();
        }

        // Property: Exposes config file path to InfoPanel
        public override string? ConfigFilePath => _configFilePath;

        // Property: Defines update interval for InfoPanel
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        // Initializes the plugin: Sets up config, authentication, and UI container
        public override void Initialize()
        {
            Debug.WriteLine("Initialize called");

            // Set paths for .ini and token files based on assembly location
            Assembly assembly = Assembly.GetExecutingAssembly();
            string basePath = assembly.ManifestModule.FullyQualifiedName;
            _configFilePath = $"{basePath}.ini";
            _tokenFilePath = Path.Combine(Path.GetDirectoryName(basePath) ?? ".", "spotifyrefresh.tmp");

            var parser = new FileIniDataParser();
            IniData config;
            if (!File.Exists(_configFilePath))
            {
                // Create a new .ini file if it doesn’t exist
                config = new IniData();
                config["Spotify Plugin"]["ClientID"] = "<your-spotify-client-id>";
                config["Spotify Plugin"]["MaxDisplayLength"] = "20";
                parser.WriteFile(_configFilePath, config);
                Debug.WriteLine("Config file created with placeholder ClientID and MaxDisplayLength.");
            }
            else
            {
                try
                {
                    // Read .ini file without locking
                    using (FileStream fileStream = new FileStream(_configFilePath!, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (StreamReader reader = new StreamReader(fileStream))
                    {
                        string fileContent = reader.ReadToEnd();
                        config = parser.Parser.Parse(fileContent);
                    }

                    // Load ClientID and MaxDisplayLength from .ini
                    _clientID = config["Spotify Plugin"]["ClientID"];
                    if (!config["Spotify Plugin"].ContainsKey("MaxDisplayLength") ||
                        !int.TryParse(config["Spotify Plugin"]["MaxDisplayLength"], out int maxLength) ||
                        maxLength <= 0)
                    {
                        config["Spotify Plugin"]["MaxDisplayLength"] = "20";
                        _maxDisplayLength = 20;
                        parser.WriteFile(_configFilePath, config);
                        Debug.WriteLine("MaxDisplayLength added or corrected to 20 in config.");
                    }
                    else
                    {
                        _maxDisplayLength = maxLength;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading config file: {ex.Message}");
                    StartAuthentication();
                    return; // Exit early if config read fails
                }
            }

            // Load tokens from spotifyrefresh.tmp if it exists
            if (File.Exists(_tokenFilePath))
            {
                try
                {
                    using (FileStream fileStream = new FileStream(_tokenFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (StreamReader reader = new StreamReader(fileStream))
                    {
                        string fileContent = reader.ReadToEnd();
                        var tokenConfig = parser.Parser.Parse(fileContent);
                        _refreshToken = tokenConfig["Spotify Tokens"]["RefreshToken"];
                        _accessToken = tokenConfig["Spotify Tokens"]["AccessToken"];
                        if (DateTime.TryParse(tokenConfig["Spotify Tokens"]["TokenExpiration"], out DateTime expiration))
                        {
                            _tokenExpiration = expiration;
                        }
                        else
                        {
                            _tokenExpiration = DateTime.MinValue; // Reset to invalid if parsing fails
                        }
                    }
                    Debug.WriteLine($"Loaded tokens from spotifyrefresh.tmp - Refresh Token: {(string.IsNullOrEmpty(_refreshToken) ? "null" : "set")}, Access Token: {(string.IsNullOrEmpty(_accessToken) ? "null" : "set")}, Expiration: {_tokenExpiration}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading token file: {ex.Message}");
                    _refreshToken = null;
                    _accessToken = null;
                    _tokenExpiration = DateTime.MinValue;
                }
            }
            else
            {
                Debug.WriteLine("No spotifyrefresh.tmp found; will create on first authentication.");
            }

            // Authenticate or reuse token if valid
            if (!string.IsNullOrEmpty(_clientID))
            {
                if (string.IsNullOrEmpty(_accessToken) || DateTime.UtcNow >= _tokenExpiration.AddSeconds(-TokenExpirationBufferSeconds) || !TryInitializeClientWithAccessToken())
                {
                    if (string.IsNullOrEmpty(_refreshToken) || !TryRefreshTokenAsync().Result)
                    {
                        StartAuthentication();
                    }
                }
                else
                {
                    // Start background token refresh if token is valid
                    StartBackgroundTokenRefresh();
                }
            }
            else
            {
                Debug.WriteLine("Spotify ClientID is not set or is invalid.");
                return;
            }

            // Register UI elements with InfoPanel
            var container = new PluginContainer("Spotify");
            container.Entries.AddRange([_currentTrack, _artist, _album, _elapsedTime, _remainingTime, _trackProgress, _coverUrl]);
            Load([container]);
        }

        // Starts a background task to periodically refresh the access token with retries
        private void StartBackgroundTokenRefresh()
        {
            Task.Run(async () =>
            {
                while (!_refreshCancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(_refreshToken) && !string.IsNullOrEmpty(_clientID) &&
                            DateTime.UtcNow >= _tokenExpiration.AddSeconds(-TokenExpirationBufferSeconds))
                        {
                            Debug.WriteLine("Background token refresh triggered due to impending expiration.");
                            int attempts = 0;
                            while (attempts < TokenRefreshMaxRetries)
                            {
                                try
                                {
                                    if (await TryRefreshTokenAsync())
                                    {
                                        break; // Success, exit retry loop
                                    }
                                }
                                catch (Exception ex)
                                {
                                    attempts++;
                                    if (attempts >= TokenRefreshMaxRetries)
                                    {
                                        Debug.WriteLine($"Background token refresh failed after {TokenRefreshMaxRetries} attempts: {ex.Message}");
                                        break; // Max retries reached
                                    }
                                    Debug.WriteLine($"Retry {attempts}/{TokenRefreshMaxRetries} for background token refresh failed: {ex.Message}");
                                    await Task.Delay(TimeSpan.FromSeconds(TokenRefreshRetryDelaySeconds * attempts), _refreshCancellationTokenSource.Token);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Unexpected error in background token refresh task: {ex.Message}");
                    }

                    // Wait before next check
                    await Task.Delay(TimeSpan.FromSeconds(TokenRefreshCheckIntervalSeconds), _refreshCancellationTokenSource.Token);
                }
            }, _refreshCancellationTokenSource.Token);
            Debug.WriteLine("Started background token refresh task.");
        }

        // Attempts to initialize SpotifyClient with stored access token if valid
        private bool TryInitializeClientWithAccessToken()
        {
            if (string.IsNullOrEmpty(_accessToken) || string.IsNullOrEmpty(_clientID) || DateTime.UtcNow >= _tokenExpiration.AddSeconds(-TokenExpirationBufferSeconds))
            {
                Debug.WriteLine("Access token missing, expired, or invalid; refresh required.");
                return false;
            }

            try
            {
                var authenticator = new PKCEAuthenticator(_clientID, new PKCETokenResponse { AccessToken = _accessToken, ExpiresIn = (int)(_tokenExpiration - DateTime.UtcNow).TotalSeconds });
                var config = SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator);
                _spotifyClient = new SpotifyClient(config);
                Debug.WriteLine("Initialized Spotify client with stored access token.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize client with stored token: {ex.Message}");
                return false;
            }
        }

        // Attempts to refresh the Spotify access token using the stored refresh token
        private async Task<bool> TryRefreshTokenAsync()
        {
            if (_refreshToken == null || _clientID == null)
            {
                Debug.WriteLine("Refresh token or ClientID missing.");
                return false;
            }

            try
            {
                var response = await new OAuthClient().RequestToken(
                    new PKCETokenRefreshRequest(_clientID, _refreshToken)
                );
                var authenticator = new PKCEAuthenticator(_clientID, response);
                var config = SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator);
                _spotifyClient = new SpotifyClient(config);

                // Store new access token and expiration
                _accessToken = response.AccessToken;
                _tokenExpiration = DateTime.UtcNow.AddSeconds(response.ExpiresIn);
                SaveTokens(_accessToken, _tokenExpiration);

                Debug.WriteLine("Successfully refreshed token.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error refreshing token: {ex.Message}");
                HandleError("Error refreshing token");
                _refreshToken = null;
                _accessToken = null;
                return false;
            }
        }

        // Saves access token and expiration to spotifyrefresh.tmp file
        private void SaveTokens(string accessToken, DateTime expiration)
        {
            try
            {
                var parser = new FileIniDataParser();
                IniData tokenConfig = new IniData();

                // Populate token data
                tokenConfig["Spotify Tokens"]["RefreshToken"] = _refreshToken ?? "";
                tokenConfig["Spotify Tokens"]["AccessToken"] = accessToken;
                tokenConfig["Spotify Tokens"]["TokenExpiration"] = expiration.ToString("o"); // ISO 8601 format

                parser.WriteFile(_tokenFilePath, tokenConfig);
                Debug.WriteLine("Tokens saved to spotifyrefresh.tmp successfully.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving tokens to spotifyrefresh.tmp: {ex.Message}");
                HandleError($"Error saving tokens: {ex.Message}");
            }
        }

        // Starts the PKCE authentication process by launching a local server and browser prompt
        private void StartAuthentication()
        {
            try
            {
                var (verifier, challenge) = PKCEUtil.GenerateCodes();
                _verifier = verifier;

                _server = new EmbedIOAuthServer(new Uri("http://localhost:5000/callback"), 5000);
                _server.AuthorizationCodeReceived += OnAuthorizationCodeReceived;
                _server.Start();

                if (_clientID == null)
                {
                    HandleError("ClientID missing");
                    return;
                }

                var loginRequest = new LoginRequest(
                    _server.BaseUri,
                    _clientID,
                    LoginRequest.ResponseType.Code
                )
                {
                    CodeChallengeMethod = "S256",
                    CodeChallenge = challenge,
                    Scope = new[] { Scopes.UserReadPlaybackState, Scopes.UserReadCurrentlyPlaying },
                };
                var uri = loginRequest.ToUri();

                Debug.WriteLine($"Authentication URI: {uri}");
                Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true });
                Debug.WriteLine("Authentication process started.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error starting authentication: {ex.Message}");
                HandleError($"Error starting authentication: {ex.Message}");
            }
        }

        // Handles the OAuth callback, exchanging the code for tokens
        private async Task OnAuthorizationCodeReceived(object sender, AuthorizationCodeResponse response)
        {
            if (_verifier == null || _clientID == null)
            {
                HandleError("Authentication setup error");
                return;
            }

            try
            {
                var initialResponse = await new OAuthClient().RequestToken(
                    new PKCETokenRequest(_clientID, response.Code, _server!.BaseUri, _verifier)
                );
                Debug.WriteLine($"Received access token: {initialResponse.AccessToken}");
                if (!string.IsNullOrEmpty(initialResponse.RefreshToken))
                {
                    _refreshToken = initialResponse.RefreshToken;
                }

                var authenticator = new PKCEAuthenticator(_clientID, initialResponse);
                var config = SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator);
                _spotifyClient = new SpotifyClient(config);

                // Store access token and expiration
                _accessToken = initialResponse.AccessToken;
                _tokenExpiration = DateTime.UtcNow.AddSeconds(initialResponse.ExpiresIn);
                SaveTokens(_accessToken, _tokenExpiration);

                await _server.Stop();
                Debug.WriteLine("Authentication completed successfully.");

                // Start background token refresh after successful auth
                StartBackgroundTokenRefresh();
            }
            catch (APIException apiEx)
            {
                HandleError("API authentication error");
                if (apiEx.Response != null && Debugger.IsAttached)
                {
                    Debug.WriteLine($"API Response Error: {apiEx.Message}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Authentication failed: {ex.Message}");
                HandleError($"Authentication failed: {ex.Message}");
            }
        }

        // Cleans up resources when the plugin is closed
        public override void Close()
        {
            _refreshCancellationTokenSource.Cancel(); // Stop background refresh task
            _server?.Dispose();
            Debug.WriteLine("Plugin closed, background refresh task stopped.");
        }

        // Loads UI elements into InfoPanel’s container system
        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("Spotify");
            container.Entries.AddRange([_currentTrack, _artist, _album, _elapsedTime, _remainingTime, _trackProgress, _coverUrl]);
            containers.Add(container);
        }

        // Synchronous update method for InfoPanel compatibility
        public override void Update()
        {
            UpdateAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        // Asynchronous update method to fetch and display Spotify data
        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            Debug.WriteLine("UpdateAsync called");
            await GetSpotifyInfo();
        }

        // Fetches and updates track info, using caching to optimize API calls
        private async Task GetSpotifyInfo()
        {
            Debug.WriteLine("GetSpotifyInfo called");

            if (_spotifyClient == null)
            {
                Debug.WriteLine("Spotify client is not initialized.");
                HandleError("Spotify client not initialized");
                return;
            }

            var now = DateTime.UtcNow;
            var timeSinceLastCall = (now - _lastApiCallTime).TotalSeconds;
            bool forceSync = false;

            // Check if the track has ended based on cached progress
            if (_lastTrackId != null && _isPlaying)
            {
                int elapsedMs = _lastProgressMs + (int)(timeSinceLastCall * 1000);
                if (elapsedMs >= _lastDurationMs)
                {
                    Debug.WriteLine("Track likely ended, forcing API sync.");
                    _trackEnded = true;
                    _trackEndTime = DateTime.UtcNow;
                    forceSync = true;
                }
            }

            // Estimate progress if within sync interval and no state change
            if (timeSinceLastCall < SyncIntervalSeconds && !forceSync && _lastTrackId != null && _isPlaying && !_pauseDetected)
            {
                int elapsedMs = _lastProgressMs + (int)(timeSinceLastCall * 1000);
                if (elapsedMs >= _lastDurationMs)
                {
                    _isPlaying = false;
                    _trackEnded = true;
                    _trackEndTime = DateTime.UtcNow;
                    SetDefaultValues("Track Ended");
                    return;
                }

                _elapsedTime.Value = TimeSpan.FromMilliseconds(elapsedMs).ToString(@"mm\:ss");
                _remainingTime.Value = TimeSpan.FromMilliseconds(_lastDurationMs - elapsedMs).ToString(@"mm\:ss");
                _trackProgress.Value = _lastDurationMs > 0 ? (float)(elapsedMs / (double)_lastDurationMs * 100) : 0.0F;
                Debug.WriteLine($"Estimated - Elapsed: {_elapsedTime.Value}, Remaining: {_remainingTime.Value}, Progress: {_trackProgress.Value:F1}%");
                return;
            }

            // Check rate limiter before making API call
            if (!_rateLimiter.TryRequest())
            {
                Debug.WriteLine("Rate limit exceeded, waiting...");
                await Task.Delay(1000);
                HandleError("Rate limit exceeded");
                return;
            }

            try
            {
                // Fetch current playback state from Spotify API
                var playback = await ExecuteWithRetry(() => _spotifyClient.Player.GetCurrentPlayback());
                _lastApiCallTime = DateTime.UtcNow;

                if (playback?.Item is FullTrack result)
                {
                    // Immediate pause detection via API flag
                    if (!playback.IsPlaying && _isPlaying)
                    {
                        Debug.WriteLine("IsPlaying false, pause detected");
                        _isPlaying = false;
                        _pauseDetected = true;
                        _pauseCount = 0;
                        _currentTrack.Value = "Paused";
                        _artist.Value = "Paused";
                        _album.Value = "Paused";
                    }
                    // Pause detection via progress stall with counter
                    else if (_isPlaying && _previousProgressMs >= 0 && Math.Abs(playback.ProgressMs - _previousProgressMs) <= ProgressToleranceMs)
                    {
                        _pauseCount++;
                        if (_pauseCount >= PauseThreshold && !_pauseDetected)
                        {
                            Debug.WriteLine("Progress stalled (pause detected), forcing API sync and stopping estimation.");
                            _isPlaying = false;
                            _pauseDetected = true;
                            _currentTrack.Value = "Paused";
                            _artist.Value = "Paused";
                            _album.Value = "Paused";
                        }
                    }
                    else
                    {
                        _pauseCount = 0;
                    }

                    // Check for resume condition
                    bool wasPaused = !_isPlaying && _pauseDetected;
                    _previousProgressMs = _lastProgressMs;
                    _lastTrackId = result.Id;
                    _lastProgressMs = playback.ProgressMs;
                    _lastDurationMs = result.DurationMs;
                    _isPlaying = playback.IsPlaying ? playback.IsPlaying : _isPlaying;

                    // Handle resume animation
                    if (wasPaused && _isPlaying && !_isResuming)
                    {
                        _isResuming = true;
                        _resumeStartTime = DateTime.UtcNow;
                        _currentTrack.Value = "Resuming...";
                        _artist.Value = "Resuming...";
                        _album.Value = "Resuming...";
                    }

                    // Update UI if playing or track changed
                    if (_isPlaying || _lastTrackId != result.Id)
                    {
                        _pauseDetected = false;
                        _trackEnded = false;
                        _lastTrackName = !string.IsNullOrEmpty(result.Name) ? result.Name : "Unknown Track";
                        _lastArtistName = string.Join(", ", result.Artists.Select(a => a.Name ?? "Unknown"));
                        _lastAlbumName = !string.IsNullOrEmpty(result.Album.Name) ? result.Album.Name : "Unknown Album";

                        _displayTrackName = CutString(_lastTrackName);
                        _displayArtistName = CutString(_lastArtistName);
                        _displayAlbumName = CutString(_lastAlbumName);

                        if (_isResuming && (DateTime.UtcNow - _resumeStartTime).TotalSeconds >= 1)
                        {
                            _isResuming = false;
                            _currentTrack.Value = _displayTrackName;
                            _artist.Value = _displayArtistName;
                            _album.Value = _displayAlbumName;
                        }
                        else if (!_isResuming)
                        {
                            _currentTrack.Value = _displayTrackName;
                            _artist.Value = _displayArtistName;
                            _album.Value = _displayAlbumName;
                        }
                    }

                    var coverArtUrl = result.Album.Images.FirstOrDefault()?.Url ?? string.Empty;
                    Debug.WriteLine($"Raw cover art URL from Spotify: {coverArtUrl}");
                    _coverUrl.Value = coverArtUrl;

                    _elapsedTime.Value = TimeSpan.FromMilliseconds(_lastProgressMs).ToString(@"mm\:ss");
                    _remainingTime.Value = TimeSpan.FromMilliseconds(_lastDurationMs - _lastProgressMs).ToString(@"mm\:ss");
                    _trackProgress.Value = _lastDurationMs > 0 ? (float)(_lastProgressMs / (double)_lastDurationMs * 100) : 0.0F;
                    Debug.WriteLine($"Synced - Track: {_currentTrack.Value}, Artist: {_artist.Value}, Album: {_album.Value}, Cover URL: {_coverUrl.Value}");
                    Debug.WriteLine($"Elapsed: {_elapsedTime.Value}, Remaining: {_remainingTime.Value}, Progress: {_trackProgress.Value:F1}%");
                }
                else
                {
                    // Handle no track playing or track end state
                    Debug.WriteLine("No track is currently playing.");
                    _lastTrackId = null;
                    _isPlaying = false;
                    _pauseDetected = false;
                    _isResuming = false;
                    _pauseCount = 0;

                    if (_trackEnded && (DateTime.UtcNow - _trackEndTime).TotalSeconds < 3)
                    {
                        _currentTrack.Value = "Track Ended";
                        _artist.Value = _lastArtistName ?? "Unknown";
                        _album.Value = _lastAlbumName ?? "Unknown";
                        _elapsedTime.Value = TimeSpan.FromMilliseconds(_lastDurationMs).ToString(@"mm\:ss");
                        _remainingTime.Value = "00:00";
                        _trackProgress.Value = 100.0F;
                    }
                    else
                    {
                        _trackEnded = false;
                        _previousProgressMs = -1;
                        _lastTrackName = null;
                        _lastArtistName = null;
                        _lastAlbumName = null;
                        _displayTrackName = null;
                        _displayArtistName = null;
                        _displayAlbumName = null;
                        _trackProgress.Value = 0.0F;
                        SetDefaultValues("No track playing");
                    }
                    return; // Exit early when no track is playing
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error fetching Spotify playback: {ex.Message}");
                HandleError("Error updating Spotify info");
            }
        }

        // Truncates a string to MaxDisplayLength, appending "..." if needed
        private string CutString(string input)
        {
            return input.Length > _maxDisplayLength ? input.Substring(0, _maxDisplayLength - 3) + "..." : input;
        }

        // Executes an API call with retry logic for rate limits or network errors
        private async Task<T?> ExecuteWithRetry<T>(Func<Task<T>> operation, int maxAttempts = 3)
        {
            int attempts = 0;
            TimeSpan delay = TimeSpan.FromSeconds(1);
            const int maxDelaySeconds = 10; // Max delay cap for rate limits

            while (attempts < maxAttempts)
            {
                try
                {
                    return await operation();
                }
                catch (APIException apiEx) when (apiEx.Response?.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (apiEx.Response.Headers.TryGetValue("Retry-After", out string? retryAfter) && int.TryParse(retryAfter, out int seconds))
                    {
                        delay = TimeSpan.FromSeconds(Math.Min(seconds, maxDelaySeconds));
                    }
                    else
                    {
                        delay = TimeSpan.FromSeconds(Math.Min(5, maxDelaySeconds));
                    }

                    attempts++;
                    if (attempts >= maxAttempts) throw;

                    Debug.WriteLine($"Rate limit hit, waiting {delay.TotalSeconds}s. Attempt {attempts}/{maxAttempts}");
                    await Task.Delay(delay);
                    delay = TimeSpan.FromSeconds(Math.Min(maxDelaySeconds, (int)delay.TotalSeconds * 2 + new Random().Next(1, 3)));
                }
                catch (APIException apiEx)
                {
                    attempts++;
                    Debug.WriteLine($"API Error: {apiEx.Message}. Attempt {attempts}/{maxAttempts}");
                    if (attempts >= maxAttempts) throw;
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    delay = TimeSpan.FromSeconds((int)delay.TotalSeconds + new Random().Next(1, 2));
                }
                catch (HttpRequestException httpEx)
                {
                    attempts++;
                    Debug.WriteLine($"HTTP Error: {httpEx.Message}. Attempt {attempts}/{maxAttempts}");
                    if (attempts >= maxAttempts) throw;
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    delay = TimeSpan.FromSeconds((int)delay.TotalSeconds + new Random().Next(1, 2));
                }
            }
            return default;
        }

        // Resets UI elements to default values on error or no playback
        // Note: Kept sync since PluginText.Value is sync; convert to async if InfoPanel adds async UI support
        private void SetDefaultValues(string message = "Unknown")
        {
            _currentTrack.Value = message;
            _artist.Value = message;
            _album.Value = message;
            _elapsedTime.Value = "00:00";
            _remainingTime.Value = "00:00";
            _trackProgress.Value = 0.0F;
            _coverUrl.Value = string.Empty;
            Debug.WriteLine($"Set default values: {message}");
            if (message == "Unknown") Debug.WriteLine("Set default values: Message set to default 'Unknown', potential unhandled error.");
        }

        // Logs errors and updates UI with error message
        // Note: Kept sync since PluginText.Value is sync; convert to async if InfoPanel adds async UI support
        private void HandleError(string errorMessage)
        {
            SetDefaultValues(errorMessage);
            Debug.WriteLine($"Error: {errorMessage}");
        }
    }

    // RateLimiter class: Ensures API requests stay within Spotify’s limits
    public class RateLimiter
    {
        private readonly int _maxRequestsPerMinute; // Max requests per minute
        private readonly TimeSpan _minuteWindow; // Window for minute limit
        private readonly int _maxRequestsPerSecond; // Max requests per second
        private readonly TimeSpan _secondWindow; // Window for second limit
        private readonly ConcurrentQueue<DateTime> _requestTimesMinute; // Timestamps for minute window
        private readonly ConcurrentQueue<DateTime> _requestTimesSecond; // Timestamps for second window

        // Constructor: Initializes rate limiter with limits and windows
        public RateLimiter(int maxRequestsPerMinute, TimeSpan minuteWindow, int maxRequestsPerSecond, TimeSpan secondWindow)
        {
            _maxRequestsPerMinute = maxRequestsPerMinute;
            _minuteWindow = minuteWindow;
            _maxRequestsPerSecond = maxRequestsPerSecond;
            _secondWindow = secondWindow;
            _requestTimesMinute = new ConcurrentQueue<DateTime>();
            _requestTimesSecond = new ConcurrentQueue<DateTime>();
        }

        // Checks if a request can be made within both minute and second limits
        public bool TryRequest()
        {
            var now = DateTime.UtcNow;

            // Enforce per-minute limit
            _requestTimesMinute.Enqueue(now);
            while (_requestTimesMinute.TryPeek(out DateTime oldest) && (now - oldest) > _minuteWindow)
            {
                _requestTimesMinute.TryDequeue(out _);
            }
            if (_requestTimesMinute.Count > _maxRequestsPerMinute) return false;

            // Enforce per-second limit
            _requestTimesSecond.Enqueue(now);
            while (_requestTimesSecond.TryPeek(out DateTime oldest) && (now - oldest) > _secondWindow)
            {
                _requestTimesSecond.TryDequeue(out _);
            }
            if (_requestTimesSecond.Count > _maxRequestsPerSecond) return false;

            return true;
        }
    }
}
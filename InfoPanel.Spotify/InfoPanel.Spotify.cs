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
 * Version: 1.0.46
 * Description: A plugin for InfoPanel to display current Spotify track information, including track name, artist, album, cover URL, elapsed time, and remaining time. Uses the Spotify Web API with PKCE authentication and updates every 1 second for UI responsiveness, with optimized API calls. Supports PluginSensor for track progression and PluginText for cover URL.
 * Changelog:
 *   - v1.0.46 (Feb 27, 2025): Fixed .ini file locking on reload and CS8604 warning.
 *     - Changes: Replaced ReadFile with FileStream and StreamReader using FileShare.Read to avoid locking, removed unnecessary parser.Dispose(), added null suppression (!) to _configFilePath to silence CS8604.
 *     - Purpose: Prevent freezes when reactivating the plugin with an existing .ini file and ensure clean compile.
 *   - For full history, see CHANGELOG.md.
 * Note: Spotify API rate limits estimated at ~180 requests/minute (https://developer.spotify.com/documentation/web-api/concepts/rate-limits).
 */

namespace InfoPanel.Spotify
{
    public class SpotifyPlugin : BasePlugin
    {
        // UI display elements (PluginText)
        private readonly PluginText _currentTrack = new("current-track", "Current Track", "-");
        private readonly PluginText _artist = new("artist", "Artist", "-");
        private readonly PluginText _album = new("album", "Album", "-");
        private readonly PluginText _elapsedTime = new("elapsed-time", "Elapsed Time", "00:00");
        private readonly PluginText _remainingTime = new("remaining-time", "Remaining Time", "00:00");
        private readonly PluginText _coverUrl = new("cover-art", "Cover URL", "");

        // UI display elements (PluginSensor)
        private readonly PluginSensor _trackProgress = new("track-progress", "Track Progress (%)", 0.0F);

        // Spotify API and authentication
        private SpotifyClient? _spotifyClient;
        private string? _verifier;
        private EmbedIOAuthServer? _server;
        private string? _apiKey;
        private string? _configFilePath;
        private string? _refreshToken;

        // Rate limiter for API calls
        private readonly RateLimiter _rateLimiter = new RateLimiter(180, TimeSpan.FromMinutes(1), 10, TimeSpan.FromSeconds(1));

        // Cache for playback state
        private string? _lastTrackId;
        private int _lastProgressMs;
        private int _previousProgressMs;
        private int _lastDurationMs;
        private bool _isPlaying;
        private DateTime _lastApiCallTime = DateTime.MinValue;
        private bool _pauseDetected;
        private int _pauseCount;
        private bool _trackEnded;
        private DateTime _trackEndTime;
        private bool _isResuming;
        private DateTime _resumeStartTime;
        private string? _lastTrackName;
        private string? _lastArtistName;
        private string? _lastAlbumName;

        // Cached display strings
        private string? _displayTrackName;
        private string? _displayArtistName;
        private string? _displayAlbumName;

        // Configurable character cutoff
        private int _maxDisplayLength = 20;

        private const int SyncIntervalSeconds = 1;
        private const int ProgressToleranceMs = 1500;
        private const int PauseThreshold = 2;

        public SpotifyPlugin()
            : base("spotify-plugin", "Spotify", "Displays the current Spotify track information. Version: 1.0.46")
        {
        }

        public override string? ConfigFilePath => _configFilePath;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        public override void Initialize()
        {
            Debug.WriteLine("Initialize called");

            Assembly assembly = Assembly.GetExecutingAssembly();
            _configFilePath = $"{assembly.ManifestModule.FullyQualifiedName}.ini";

            var parser = new FileIniDataParser();
            IniData config;
            if (!File.Exists(_configFilePath))
            {
                config = new IniData();
                config["Spotify Plugin"]["APIKey"] = "<your-spotify-api-key>";
                config["Spotify Plugin"]["MaxDisplayLength"] = "20";
                parser.WriteFile(_configFilePath, config);
                Debug.WriteLine("Config file created with placeholder API key and MaxDisplayLength.");
            }
            else
            {
                try
                {
                    using (FileStream fileStream = new FileStream(_configFilePath!, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (StreamReader reader = new StreamReader(fileStream))
                    {
                        string fileContent = reader.ReadToEnd();
                        config = parser.Parser.Parse(fileContent);
                    }

                    _apiKey = config["Spotify Plugin"]["APIKey"];
                    _refreshToken = config["Spotify Plugin"]["RefreshToken"];

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

                    Debug.WriteLine($"API Key: {_apiKey}, Refresh Token: {(string.IsNullOrEmpty(_refreshToken) ? "null" : "set")}, MaxDisplayLength: {_maxDisplayLength}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading config file: {ex.Message}");
                    StartAuthentication();
                    return;
                }
            }

            if (!string.IsNullOrEmpty(_apiKey))
            {
                if (string.IsNullOrEmpty(_refreshToken) || !TryRefreshTokenAsync().Result)
                {
                    StartAuthentication();
                }
            }
            else
            {
                Debug.WriteLine("Spotify API Key is not set or is invalid.");
                return;
            }

            var container = new PluginContainer("Spotify");
            container.Entries.AddRange([_currentTrack, _artist, _album, _elapsedTime, _remainingTime, _trackProgress, _coverUrl]);
            Load([container]);
        }

        private async Task<bool> TryRefreshTokenAsync()
        {
            if (_refreshToken == null || _apiKey == null)
            {
                Debug.WriteLine("Refresh token or API key missing.");
                return false;
            }

            try
            {
                var response = await new OAuthClient().RequestToken(
                    new PKCETokenRefreshRequest(_apiKey, _refreshToken)
                );
                var authenticator = new PKCEAuthenticator(_apiKey, response);
                var config = SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator);
                _spotifyClient = new SpotifyClient(config);
                Debug.WriteLine("Successfully refreshed token.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error refreshing token: {ex.Message}");
                HandleError("Error refreshing token");
                _refreshToken = null;
                return false;
            }
        }

        private void StartAuthentication()
        {
            try
            {
                var (verifier, challenge) = PKCEUtil.GenerateCodes();
                _verifier = verifier;

                _server = new EmbedIOAuthServer(new Uri("http://localhost:5000/callback"), 5000);
                _server.AuthorizationCodeReceived += OnAuthorizationCodeReceived;
                _server.Start();

                if (_apiKey == null)
                {
                    HandleError("API Key missing");
                    return;
                }

                var loginRequest = new LoginRequest(
                    _server.BaseUri,
                    _apiKey,
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

        private async Task OnAuthorizationCodeReceived(object sender, AuthorizationCodeResponse response)
        {
            if (_verifier == null || _apiKey == null)
            {
                HandleError("Authentication setup error");
                return;
            }

            try
            {
                var initialResponse = await new OAuthClient().RequestToken(
                    new PKCETokenRequest(_apiKey, response.Code, _server!.BaseUri, _verifier)
                );
                Debug.WriteLine($"Received access token: {initialResponse.AccessToken}");
                if (!string.IsNullOrEmpty(initialResponse.RefreshToken))
                {
                    _refreshToken = initialResponse.RefreshToken;
                    SaveRefreshToken(_refreshToken);
                    Debug.WriteLine("Refresh token saved.");
                }

                var authenticator = new PKCEAuthenticator(_apiKey, initialResponse);
                var config = SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator);
                _spotifyClient = new SpotifyClient(config);
                await _server.Stop();
                Debug.WriteLine("Authentication completed successfully.");
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

        private void SaveRefreshToken(string token)
        {
            try
            {
                var parser = new FileIniDataParser();
                IniData config;

                using (FileStream fileStream = new FileStream(_configFilePath!, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (StreamReader reader = new StreamReader(fileStream))
                {
                    string fileContent = reader.ReadToEnd();
                    config = parser.Parser.Parse(fileContent);
                }

                config["Spotify Plugin"]["RefreshToken"] = token;
                parser.WriteFile(_configFilePath, config);
                Debug.WriteLine("Refresh token saved successfully.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving refresh token: {ex.Message}");
                HandleError($"Error saving refresh token: {ex.Message}");
            }
        }

        public override void Close()
        {
            _server?.Dispose();
        }

        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("Spotify");
            container.Entries.AddRange([_currentTrack, _artist, _album, _elapsedTime, _remainingTime, _trackProgress, _coverUrl]);
            containers.Add(container);
        }

        public override void Update()
        {
            UpdateAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            Debug.WriteLine("UpdateAsync called");
            await GetSpotifyInfo();
        }

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

            if (!_rateLimiter.TryRequest())
            {
                Debug.WriteLine("Rate limit exceeded, waiting...");
                await Task.Delay(1000);
                HandleError("Rate limit exceeded");
                return;
            }

            try
            {
                var playback = await ExecuteWithRetry(() => _spotifyClient.Player.GetCurrentPlayback());
                _lastApiCallTime = DateTime.UtcNow;

                if (playback?.Item is FullTrack result)
                {
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

                    bool wasPaused = !_isPlaying && _pauseDetected;
                    _previousProgressMs = _lastProgressMs;
                    _lastTrackId = result.Id;
                    _lastProgressMs = playback.ProgressMs;
                    _lastDurationMs = result.DurationMs;
                    _isPlaying = playback.IsPlaying ? playback.IsPlaying : _isPlaying;

                    if (wasPaused && _isPlaying && !_isResuming)
                    {
                        _isResuming = true;
                        _resumeStartTime = DateTime.UtcNow;
                        _currentTrack.Value = "Resuming...";
                        _artist.Value = "Resuming...";
                        _album.Value = "Resuming...";
                    }

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
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error fetching Spotify playback: {ex.Message}");
                HandleError("Error updating Spotify info");
            }
        }

        private string CutString(string input)
        {
            return input.Length > _maxDisplayLength ? input.Substring(0, _maxDisplayLength - 3) + "..." : input;
        }

        private async Task<T?> ExecuteWithRetry<T>(Func<Task<T>> operation, int maxAttempts = 3)
        {
            int attempts = 0;
            TimeSpan delay = TimeSpan.FromSeconds(1);
            const int maxDelaySeconds = 10;

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

        private void HandleError(string errorMessage)
        {
            SetDefaultValues(errorMessage);
            Debug.WriteLine($"Error: {errorMessage}");
        }
    }

    public class RateLimiter
    {
        private readonly int _maxRequestsPerMinute;
        private readonly TimeSpan _minuteWindow;
        private readonly int _maxRequestsPerSecond;
        private readonly TimeSpan _secondWindow;
        private readonly ConcurrentQueue<DateTime> _requestTimesMinute;
        private readonly ConcurrentQueue<DateTime> _requestTimesSecond;

        public RateLimiter(int maxRequestsPerMinute, TimeSpan minuteWindow, int maxRequestsPerSecond, TimeSpan secondWindow)
        {
            _maxRequestsPerMinute = maxRequestsPerMinute;
            _minuteWindow = minuteWindow;
            _maxRequestsPerSecond = maxRequestsPerSecond;
            _secondWindow = secondWindow;
            _requestTimesMinute = new ConcurrentQueue<DateTime>();
            _requestTimesSecond = new ConcurrentQueue<DateTime>();
        }

        public bool TryRequest()
        {
            var now = DateTime.UtcNow;

            _requestTimesMinute.Enqueue(now);
            while (_requestTimesMinute.TryPeek(out DateTime oldest) && (now - oldest) > _minuteWindow)
            {
                _requestTimesMinute.TryDequeue(out _);
            }
            if (_requestTimesMinute.Count > _maxRequestsPerMinute) return false;

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
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Plugins;
using InfoPanel.Spotify.Models;
using InfoPanel.Spotify.Services;
using IniParser;
using IniParser.Model;
using SpotifyAPI.Web;

/*
 * Plugin: Spotify Info - SpotifyPlugin
 * Version: 1.0.91
 * Description: A plugin for InfoPanel to display current Spotify track information, including track name, artist, album, cover URL, elapsed time, and remaining time. Uses the Spotify Web API with PKCE authentication and updates every 1 second for UI responsiveness, with optimized API calls. Supports PluginSensor for track progression and auth state, and PluginText for cover URL.
 * Changelog:
  *   - v1.0.91 (June 10, 2025): Fixed background refresh timing.
 *     - **Changes**: Changed callback url from localhost to 127.0.0.1 to comply with new Spotify guidelines: https://developer.spotify.com/documentation/web-api/concepts/redirect_uri
 *   - v1.0.90 (Mar 2, 2025): Fixed background refresh timing.
 *     - **Changes**: Reduced `TokenRefreshCheckIntervalSeconds` from 1500s to 60s to ensure timely token refresh before expiry. Fixed `_trackProgress.Value` type mismatch from string to float in `GetSpotifyInfo`.
 *     - **Purpose**: Ensures background refresh triggers reliably within 60s buffer, preventing token expiry failures; corrects compile error CS0029.
 *   - v1.0.88 (Mar 2, 2025): Style cleanup.
 *     - **Changes**: Simplified `SetDefaultValues`/`HandleError`, removed unused `using`s.
 *     - **Purpose**: Reduce redundancy, declutter code—no functional impact.
 *   - v1.0.87 (Mar 2, 2025): Polished initialization and error handling.
 *     - **Changes**: Fixed redundant `TryInitializeClientWithAccessToken` calls, used `AggregateException` in `ExecuteWithRetry`, restricted `_forceInvalidGrant` to debug builds.
 *     - **Purpose**: Cleaner logs, better error context, production safety.
 *   - v1.0.86 (Mar 2, 2025): Fixed token update and refresh timing.
 *     - **Changes**: Enhanced refresh logging, ensured `SaveTokens` updates `.tmp`, adjusted `forceInvalidGrant` timing.
 *     - **Purpose**: Fix `.tmp` not updating, clarify refresh behavior, avoid early triggers.
 *   - For full history, see CHANGELOG.md.
 * Note: Spotify API rate limits estimated at ~180 requests/minute (https://developer.spotify.com/documentation/web-api/concepts/rate-limits).
 */

namespace InfoPanel.Spotify
{
    public class SpotifyPlugin : BasePlugin
    {
        // UI display elements (PluginText) for InfoPanel
        private readonly PluginText _currentTrack = new("current-track", "Current Track", "-");
        private readonly PluginText _artist = new("artist", "Artist", "-");
        private readonly PluginText _album = new("album", "Album", "-");
        private readonly PluginText _elapsedTime = new("elapsed-time", "Elapsed Time", "00:00");
        private readonly PluginText _remainingTime = new("remaining-time", "Remaining Time", "00:00");
        private readonly PluginText _coverUrl = new("cover-art", "Cover URL", "");

        // UI display elements (PluginSensor) for InfoPanel
        private readonly PluginSensor _trackProgress = new("track-progress", "Track Progress (%)", 0.0F);
        private readonly PluginSensor _authState = new("auth-state", "Auth State", (float)AuthState.NotAuthenticated); // 0=NotAuth, 1=Authenticating, 2=Authenticated, 3=Error

        // Services for Spotify interaction
        private SpotifyAuthService? _authService;
        private SpotifyPlaybackService? _playbackService;
        private RateLimiter? _rateLimiter;

        // Configuration
        private string? _configFilePath;
        private string? _tokenFilePath;
        private string? _clientID;
        private int _maxDisplayLength = 20;
        private bool _forceInvalidGrant;

        // Background refresh task (non-nullable, always initialized)
        private CancellationTokenSource _refreshCancellationTokenSource;

        // Constants for timing and detection thresholds
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        // Constructor: Initializes the plugin with metadata
        public SpotifyPlugin()
            : base("spotify-plugin", "Spotify", "Displays the current Spotify track information. Version: 1.0.91")
        {
            _refreshCancellationTokenSource = new CancellationTokenSource();
        }

        public override string? ConfigFilePath => _configFilePath;

        // Initializes the plugin (reentrant, with immediate sync refresh for expired or near-expiry tokens)
        public override void Initialize()
        {
            Debug.WriteLine($"Initialize called at UTC: {DateTime.UtcNow.ToString("o")}");
            // Ensure clean state for reentrancy
            if (!_refreshCancellationTokenSource.IsCancellationRequested)
            {
                _refreshCancellationTokenSource.Cancel();
            }
            _refreshCancellationTokenSource = new CancellationTokenSource();

            Assembly assembly = Assembly.GetExecutingAssembly();
            string basePath = assembly.ManifestModule.FullyQualifiedName;
            _configFilePath = $"{basePath}.ini";
            _tokenFilePath = Path.Combine(Path.GetDirectoryName(basePath) ?? ".", "spotifyrefresh.tmp");
            Debug.WriteLine($"Config file path: {_configFilePath}");
            Debug.WriteLine($"Token file path: {_tokenFilePath}");

            // Create rate limiter
            _rateLimiter = new RateLimiter(180, TimeSpan.FromMinutes(1), 10, TimeSpan.FromSeconds(1));
            
            LoadConfigFile();
            
            if (string.IsNullOrEmpty(_clientID))
            {
                Debug.WriteLine("Spotify ClientID is not set or is invalid.");
                _authState.Value = (float)AuthState.Error;
                return;
            }
            
            // Initialize services
            _authService = new SpotifyAuthService(_clientID, _tokenFilePath);
            _playbackService = new SpotifyPlaybackService(_rateLimiter);
            
            // Set up event handlers
            _authService.AuthStateChanged += (sender, state) => _authState.Value = (float)state;
            _authService.ClientInitialized += (sender, client) => _playbackService.SetClient(client);
            _playbackService.PlaybackUpdated += OnPlaybackUpdated;
            _playbackService.PlaybackError += OnPlaybackError;
            
            // Set debug flag if configured
#if DEBUG
            _authService.SetForceInvalidGrant(_forceInvalidGrant);
#endif

            bool isValid = _authService.TryInitializeClientWithAccessToken();
            bool isNearExpiry = _authService.TokenExpiration != DateTime.MinValue && 
                DateTime.UtcNow >= _authService.TokenExpiration.AddSeconds(-SpotifyAuthService.TokenNearExpiryThresholdSeconds);
                
            if (!isValid || isNearExpiry)
            {
                Debug.WriteLine($"Token check - Valid init: {isValid}, Near expiry: {isNearExpiry} " +
                                $"(Expiration UTC: {_authService.TokenExpiration.ToString("o")}, " +
                                $"Now UTC: {DateTime.UtcNow.ToString("o")}); " +
                                $"attempting immediate sync refresh...");
                                
                if (!string.IsNullOrEmpty(_authService.RefreshToken))
                {
                    // Synchronous refresh to ensure token is valid before proceeding
                    if (Task.Run(() => _authService.TryRefreshTokenAsync()).GetAwaiter().GetResult())
                    {
                        Debug.WriteLine("Sync token refresh succeeded in Initialize; starting background task.");
                        _authState.Value = (float)AuthState.Authenticated;
                        StartBackgroundTokenRefresh();
                    }
                    else
                    {
                        Debug.WriteLine("Sync token refresh failed in Initialize; waiting for user to reauthorize.");
                        _authState.Value = (float)AuthState.NotAuthenticated;
                    }
                }
                else
                {
                    Debug.WriteLine("No refresh token available; waiting for user to authorize via button.");
                    _authState.Value = (float)AuthState.NotAuthenticated;
                }
            }
            else
            {
                Debug.WriteLine("Token valid and not near expiry; starting background refresh.");
                _authState.Value = (float)AuthState.Authenticated;
                StartBackgroundTokenRefresh();
            }
        }

        private void LoadConfigFile()
        {
            var parser = new FileIniDataParser();
            IniData config;
            
            if (!File.Exists(_configFilePath))
            {
                config = new IniData();
                config["Spotify Plugin"]["ClientID"] = "<your-spotify-client-id>";
                config["Spotify Plugin"]["MaxDisplayLength"] = "20";
                config["Spotify Plugin"]["ForceInvalidGrant"] = "false";
                parser.WriteFile(_configFilePath, config);
                Debug.WriteLine("Config file created with placeholder ClientID, MaxDisplayLength, and ForceInvalidGrant.");
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

#if DEBUG
                    // Load ForceInvalidGrant from .ini only in debug builds
                    if (config["Spotify Plugin"].ContainsKey("ForceInvalidGrant") &&
                        bool.TryParse(config["Spotify Plugin"]["ForceInvalidGrant"], out bool forceInvalid))
                    {
                        _forceInvalidGrant = forceInvalid;
                    }
#endif
                    Debug.WriteLine($"Loaded ClientID: {_clientID}, MaxDisplayLength: {_maxDisplayLength}, ForceInvalidGrant: {_forceInvalidGrant}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading config file: {ex.Message}");
                    _authState.Value = (float)AuthState.Error;
                }
            }
        }

        // Loads UI containers as required by BasePlugin
        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("Spotify");
            container.Entries.AddRange([_currentTrack, _artist, _album, _elapsedTime, _remainingTime, _trackProgress, _authState, _coverUrl]);
            containers.Add(container);
        }

        // Button to manually start Spotify authentication
        [PluginAction("Authorize with Spotify")]
        public void StartSpotifyAuth()
        {
            Debug.WriteLine($"Authorize with Spotify button clicked at UTC: {DateTime.UtcNow.ToString("o")}; initiating authentication...");
            _authState.Value = (float)AuthState.Authenticating;
            if (_authService != null)
            {
                _authService.StartAuthentication();
            }
            else 
            {
                Debug.WriteLine("Auth service is not initialized.");
                _authState.Value = (float)AuthState.Error;
            }
        }

        // Starts a background task to periodically refresh the access token with retries
        private void StartBackgroundTokenRefresh()
        {
            Debug.WriteLine($"Entering StartBackgroundTokenRefresh at UTC: {DateTime.UtcNow.ToString("o")}");
            
            if (_authService == null)
            {
                Debug.WriteLine("Auth service is not initialized, cannot start background refresh.");
                return;
            }
            
            Task.Run(async () =>
            {
                Debug.WriteLine($"Background refresh task started at UTC: {DateTime.UtcNow.ToString("o")}");
                
                while (!_refreshCancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        Debug.WriteLine($"Background refresh loop iteration at UTC: {DateTime.UtcNow.ToString("o")}");
                        
                        if (_authService.Client == null || _authService.RefreshFailed)
                        {
                            Debug.WriteLine("Skipping refresh due to missing client or previous failure.");
                            continue;
                        }

                        DateTime now = DateTime.UtcNow;
                        DateTime refreshThreshold = _authService.TokenExpiration.AddSeconds(-SpotifyAuthService.TokenExpirationBufferSeconds);
                        
                        if (!string.IsNullOrEmpty(_authService.RefreshToken) && now >= refreshThreshold)
                        {
                            Debug.WriteLine($"Background token refresh triggered at UTC: {now.ToString("o")}; " +
                                           $"token expires at UTC: {_authService.TokenExpiration.ToString("o")}; " +
                                           $"refresh threshold UTC: {refreshThreshold.ToString("o")}");
                                           
                            int attempts = 0;
                            while (attempts < SpotifyAuthService.TokenRefreshMaxRetries)
                            {
                                try
                                {
                                    if (await _authService.TryRefreshTokenAsync())
                                    {
                                        Debug.WriteLine($"Background refresh succeeded at UTC: {DateTime.UtcNow.ToString("o")}; " +
                                                       $"new expiry UTC: {_authService.TokenExpiration.ToString("o")}");
                                        break;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    attempts++;
                                    if (attempts >= SpotifyAuthService.TokenRefreshMaxRetries)
                                    {
                                        Debug.WriteLine($"Background token refresh failed after " +
                                                      $"{SpotifyAuthService.TokenRefreshMaxRetries} attempts at UTC: " +
                                                      $"{DateTime.UtcNow.ToString("o")}: {ex.Message}");
                                        break;
                                    }
                                    
                                    Debug.WriteLine($"Retry {attempts}/{SpotifyAuthService.TokenRefreshMaxRetries} for " +
                                                  $"background token refresh failed at UTC: {DateTime.UtcNow.ToString("o")}: {ex.Message}");
                                                  
                                    await Task.Delay(TimeSpan.FromSeconds(SpotifyAuthService.TokenRefreshRetryDelaySeconds * attempts), 
                                                   _refreshCancellationTokenSource.Token);
                                }
                            }
                        }
                        else
                        {
                            Debug.WriteLine($"Background refresh check at UTC: {now.ToString("o")}; " +
                                          $"token still valid until UTC: {_authService.TokenExpiration.ToString("o")}; " +
                                          $"refresh threshold UTC: {refreshThreshold.ToString("o")}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Unexpected error in background token refresh task at UTC: {DateTime.UtcNow.ToString("o")}: {ex.Message}");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(SpotifyAuthService.TokenRefreshCheckIntervalSeconds), _refreshCancellationTokenSource.Token);
                }
                
                Debug.WriteLine($"Background refresh task stopped at UTC: {DateTime.UtcNow.ToString("o")}");
            }, _refreshCancellationTokenSource.Token);
            
            Debug.WriteLine($"Background refresh task launched at UTC: {DateTime.UtcNow.ToString("o")}");
        }

        // Cleans up resources when the plugin is closed (reentrant)
        public override void Close()
        {
            Debug.WriteLine($"Close called at UTC: {DateTime.UtcNow.ToString("o")}");
            
            if (!_refreshCancellationTokenSource.IsCancellationRequested)
            {
                _refreshCancellationTokenSource.Cancel();
            }
            
            _authService?.Close();
            _playbackService?.Reset();
            
            _authState.Value = (float)AuthState.NotAuthenticated;
            SetDefaultValues("Plugin Closed");
            
            Debug.WriteLine("Plugin closed, background refresh task stopped.");
        }

        // Synchronous update method required by BasePlugin
        public override void Update()
        {
            UpdateAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        // Asynchronous update method to fetch and display Spotify data
        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            Debug.WriteLine($"UpdateAsync called at UTC: {DateTime.UtcNow.ToString("o")}");
            
            if (_authService?.RefreshFailed == true)
            {
                Debug.WriteLine("Skipping update due to previous refresh failure.");
                _currentTrack.Value = "Reauthorize Required"; // Hint to user
                return;
            }
            
            if (_playbackService != null)
            {
                await _playbackService.GetCurrentPlaybackAsync();
            }
        }

        // Handle playback updates
        private void OnPlaybackUpdated(object sender, PlaybackInfo info)
        {
            _currentTrack.Value = CutString(info.TrackName ?? "Unknown");
            _artist.Value = CutString(info.ArtistName ?? "Unknown");
            _album.Value = CutString(info.AlbumName ?? "Unknown");
            _elapsedTime.Value = TimeSpan.FromMilliseconds(info.ProgressMs).ToString(@"mm\:ss");
            _remainingTime.Value = TimeSpan.FromMilliseconds(info.DurationMs - info.ProgressMs).ToString(@"mm\:ss");
            _trackProgress.Value = info.DurationMs > 0 ? (float)(info.ProgressMs / (double)info.DurationMs * 100) : 0.0F;
            _coverUrl.Value = info.CoverUrl ?? string.Empty;
        }

        // Handle playback errors
        private void OnPlaybackError(object sender, string errorMessage)
        {
            SetDefaultValues(errorMessage);
        }

        // Truncates a string to MaxDisplayLength, appending "..." if needed
        private string CutString(string input)
        {
            return input.Length > _maxDisplayLength ? input.Substring(0, _maxDisplayLength - 3) + "..." : input;
        }

        // Resets UI elements to default values with provided message
        private void SetDefaultValues(string message)
        {
            _currentTrack.Value = message;
            _artist.Value = message;
            _album.Value = message;
            _elapsedTime.Value = "00:00";
            _remainingTime.Value = "00:00";
            _trackProgress.Value = 0.0F;
            _coverUrl.Value = string.Empty;
            Debug.WriteLine($"Set default values: {message}");
        }
    }
}
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Plugins;
using IniParser;
using IniParser.Model;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;

/*
 * Plugin: Spotify Info - SpotifyPlugin
 * Version: 1.0.90
 * Description: A plugin for InfoPanel to display current Spotify track information, including track name, artist, album, cover URL, elapsed time, and remaining time. Uses the Spotify Web API with PKCE authentication and updates every 1 second for UI responsiveness, with optimized API calls. Supports PluginSensor for track progression and auth state, and PluginText for cover URL.
 * Changelog:
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

        // Enum for auth state tracking
        private enum AuthState
        {
            NotAuthenticated = 0,
            Authenticating = 1,
            Authenticated = 2,
            Error = 3
        }

        // Spotify API and authentication fields
        private SpotifyClient? _spotifyClient;
        private string? _verifier;
        private EmbedIOAuthServer? _server;
        private string? _clientID;
        private string? _configFilePath;
        private string? _tokenFilePath;
        private string? _refreshToken;
        private string? _accessToken;
        private DateTime _tokenExpiration;
        private bool _refreshFailed; // Flag to stop spam after refresh failure
        private bool _forceInvalidGrant; // Debug flag to simulate invalid_grant, loaded from .ini in debug only

        // Background refresh task (non-nullable, always initialized)
        private CancellationTokenSource _refreshCancellationTokenSource;

        // Rate limiter to manage API request frequency
        private readonly RateLimiter _rateLimiter = new RateLimiter(180, TimeSpan.FromMinutes(1), 10, TimeSpan.FromSeconds(1));

        // Cache for playback state to reduce API calls
        private string? _lastTrackId;
        private int _lastProgressMs;
        private int _previousProgressMs;
        private int _lastDurationMs;
        private bool _isPlaying;
        private DateTime _lastApiCallTime = DateTime.MinValue;
        private bool _pauseDetected;
        private int _pauseCount;
        private bool _trackEnded;
        private DateTime _trackEndTime; // Retained for 3s "Track Ended" UX; could simplify if not needed
        private bool _isResuming; // Persists until first successful API call

        private string? _lastTrackName;
        private string? _lastArtistName;
        private string? _lastAlbumName;

        // Cached display strings for optimized UI updates
        private string? _displayTrackName;
        private string? _displayArtistName;
        private string? _displayAlbumName;

        // Configurable settings
        private int _maxDisplayLength = 20;

        // Constants for timing and detection thresholds
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);
        private const int ProgressToleranceMs = 1500;
        private const int PauseThreshold = 2;
        private const int TokenExpirationBufferSeconds = 60;
        private const int TokenRefreshCheckIntervalSeconds = 60; // ~1 minute for frequent checks, hits 60s buffer
        private const int TokenRefreshMaxRetries = 3;
        private const int TokenRefreshRetryDelaySeconds = 5;
        private const int TokenNearExpiryThresholdSeconds = 300; // 5 minutes

        // Constructor: Initializes the plugin with metadata
        public SpotifyPlugin()
            : base("spotify-plugin", "Spotify", "Displays the current Spotify track information. Version: 1.0.90")
        {
            _refreshCancellationTokenSource = new CancellationTokenSource();
            _refreshFailed = false;
#if DEBUG
            _forceInvalidGrant = false; // Default, overridden by .ini in debug
#else
            _forceInvalidGrant = false; // Hardcoded false in release
#endif
        }

        public override string? ConfigFilePath => _configFilePath;

        // Initializes the plugin (reentrant, with immediate sync refresh for expired or near-expiry tokens)
        public override void Initialize()
        {
            Debug.WriteLine($"Initialize called at UTC: {DateTime.UtcNow.ToString("o")}; forceInvalidGrant: {_forceInvalidGrant}");
            // Ensure clean state for reentrancy
            if (!_refreshCancellationTokenSource.IsCancellationRequested)
            {
                _refreshCancellationTokenSource.Cancel();
            }
            _refreshCancellationTokenSource = new CancellationTokenSource();
            _refreshFailed = false; // Reset on init

            Assembly assembly = Assembly.GetExecutingAssembly();
            string basePath = assembly.ManifestModule.FullyQualifiedName;
            _configFilePath = $"{basePath}.ini";
            _tokenFilePath = Path.Combine(Path.GetDirectoryName(basePath) ?? ".", "spotifyrefresh.tmp");
            Debug.WriteLine($"Config file path: {_configFilePath}");
            Debug.WriteLine($"Token file path: {_tokenFilePath}");

            var parser = new FileIniDataParser();
            IniData config;
            if (!File.Exists(_configFilePath))
            {
                config = new IniData();
                config["Spotify Plugin"]["ClientID"] = "<your-spotify-client-id>";
                config["Spotify Plugin"]["MaxDisplayLength"] = "20";
                config["Spotify Plugin"]["ForceInvalidGrant"] = "false"; // Add debug flag to .ini
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
                    return; // User must click button for initial auth
                }
            }

            if (File.Exists(_tokenFilePath))
            {
                try
                {
                    Debug.WriteLine($"Token file last modified UTC: {File.GetLastWriteTimeUtc(_tokenFilePath).ToString("o")}");
                    using (FileStream fileStream = new FileStream(_tokenFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (StreamReader reader = new StreamReader(fileStream))
                    {
                        string fileContent = reader.ReadToEnd();
                        Debug.WriteLine($"Raw .tmp content: {fileContent}");
                        var tokenConfig = parser.Parser.Parse(fileContent);
                        _refreshToken = tokenConfig["Spotify Tokens"]["RefreshToken"];
                        _accessToken = tokenConfig["Spotify Tokens"]["AccessToken"];
                        string expirationStr = tokenConfig["Spotify Tokens"]["TokenExpiration"];
                        if (!DateTime.TryParse(expirationStr, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime expiration))
                        {
                            Debug.WriteLine($"Invalid TokenExpiration format in .tmp: '{expirationStr}'; resetting to MinValue.");
                            _tokenExpiration = DateTime.MinValue;
                        }
                        else
                        {
                            _tokenExpiration = expiration.ToUniversalTime(); // Ensure UTC
                        }

                        if (string.IsNullOrEmpty(_refreshToken) || string.IsNullOrEmpty(_accessToken))
                        {
                            Debug.WriteLine("Tokens in .tmp are null or empty; resetting.");
                            _refreshToken = null;
                            _accessToken = null;
                            _tokenExpiration = DateTime.MinValue;
                        }
                    }
                    Debug.WriteLine($"Loaded tokens from spotifyrefresh.tmp - Refresh Token: {(string.IsNullOrEmpty(_refreshToken) ? "null" : "set")}, Access Token: {(string.IsNullOrEmpty(_accessToken) ? "null" : "set")}, Expiration UTC: {_tokenExpiration.ToString("o")}, Kind: {_tokenExpiration.Kind}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading token file: {ex.Message}");
                    _refreshToken = null;
                    _accessToken = null;
                    _tokenExpiration = DateTime.MinValue;
                    _authState.Value = (float)AuthState.Error;
                }
            }
            else
            {
                Debug.WriteLine("No spotifyrefresh.tmp found; will create on first authentication via button.");
            }

            if (!string.IsNullOrEmpty(_clientID))
            {
                Debug.WriteLine("Checking token usability...");
                if (string.IsNullOrEmpty(_accessToken))
                {
                    Debug.WriteLine("No access token; waiting for user to authorize via button.");
                    _authState.Value = (float)AuthState.NotAuthenticated;
                }
                else
                {
                    bool isValid = TryInitializeClientWithAccessToken();
                    bool isNearExpiry = _tokenExpiration != DateTime.MinValue && DateTime.UtcNow >= _tokenExpiration.AddSeconds(-TokenNearExpiryThresholdSeconds);
                    if (!isValid || isNearExpiry)
                    {
                        Debug.WriteLine($"Token check - Valid init: {isValid}, Near expiry: {isNearExpiry} (Expiration UTC: {_tokenExpiration.ToString("o")}, Now UTC: {DateTime.UtcNow.ToString("o")}); attempting immediate sync refresh...");
                        if (!string.IsNullOrEmpty(_refreshToken))
                        {
                            // Synchronous refresh to ensure token is valid before proceeding
                            if (Task.Run(() => TryRefreshTokenAsync()).GetAwaiter().GetResult())
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
            }
            else
            {
                Debug.WriteLine("Spotify ClientID is not set or is invalid.");
                _authState.Value = (float)AuthState.Error;
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
            _refreshFailed = false; // Reset on manual auth
            _authState.Value = (float)AuthState.Authenticating;
            StartAuthentication();
        }

        // Starts a background task to periodically refresh the access token with retries
        private void StartBackgroundTokenRefresh()
        {
            Debug.WriteLine($"Entering StartBackgroundTokenRefresh at UTC: {DateTime.UtcNow.ToString("o")}; forceInvalidGrant: {_forceInvalidGrant}");
            Task.Run(async () =>
            {
                Debug.WriteLine($"Background refresh task started at UTC: {DateTime.UtcNow.ToString("o")}; forceInvalidGrant: {_forceInvalidGrant}");
                while (!_refreshCancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        Debug.WriteLine($"Background refresh loop iteration at UTC: {DateTime.UtcNow.ToString("o")}");
                        if (_spotifyClient == null)
                        {
                            Debug.WriteLine($"Spotify client is null at UTC: {DateTime.UtcNow.ToString("o")}; skipping refresh attempt.");
                            continue;
                        }

                        if (_refreshFailed)
                        {
                            Debug.WriteLine($"Refresh previously failed at UTC: {DateTime.UtcNow.ToString("o")}; awaiting manual reauthorization.");
                            continue;
                        }

                        DateTime now = DateTime.UtcNow;
                        DateTime refreshThreshold = _tokenExpiration.AddSeconds(-TokenExpirationBufferSeconds);
                        if (!string.IsNullOrEmpty(_refreshToken) && !string.IsNullOrEmpty(_clientID) && now >= refreshThreshold)
                        {
                            Debug.WriteLine($"Background token refresh triggered at UTC: {now.ToString("o")}; token expires at UTC: {_tokenExpiration.ToString("o")}; refresh threshold UTC: {refreshThreshold.ToString("o")}; forceInvalidGrant: {_forceInvalidGrant}");
                            int attempts = 0;
                            while (attempts < TokenRefreshMaxRetries)
                            {
                                try
                                {
                                    if (await TryRefreshTokenAsync())
                                    {
                                        Debug.WriteLine($"Background refresh succeeded at UTC: {DateTime.UtcNow.ToString("o")}; new expiry UTC: {_tokenExpiration.ToString("o")}");
                                        break;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    attempts++;
                                    if (attempts >= TokenRefreshMaxRetries)
                                    {
                                        Debug.WriteLine($"Background token refresh failed after {TokenRefreshMaxRetries} attempts at UTC: {DateTime.UtcNow.ToString("o")}: {ex.Message}");
                                        _refreshFailed = true; // Mark as failed to stop spam
                                        _authState.Value = (float)AuthState.Error;
                                        break;
                                    }
                                    Debug.WriteLine($"Retry {attempts}/{TokenRefreshMaxRetries} for background token refresh failed at UTC: {DateTime.UtcNow.ToString("o")}: {ex.Message}");
                                    await Task.Delay(TimeSpan.FromSeconds(TokenRefreshRetryDelaySeconds * attempts), _refreshCancellationTokenSource.Token);
                                }
                            }
                        }
                        else
                        {
                            Debug.WriteLine($"Background refresh check at UTC: {now.ToString("o")}; token still valid until UTC: {_tokenExpiration.ToString("o")}; refresh threshold UTC: {refreshThreshold.ToString("o")}; forceInvalidGrant: {_forceInvalidGrant}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Unexpected error in background token refresh task at UTC: {DateTime.UtcNow.ToString("o")}: {ex.Message}");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(TokenRefreshCheckIntervalSeconds), _refreshCancellationTokenSource.Token);
                }
                Debug.WriteLine($"Background refresh task stopped at UTC: {DateTime.UtcNow.ToString("o")}");
            }, _refreshCancellationTokenSource.Token);
            Debug.WriteLine($"Background refresh task launched at UTC: {DateTime.UtcNow.ToString("o")}");
        }

        // Attempts to initialize SpotifyClient with stored access token if valid
        private bool TryInitializeClientWithAccessToken()
        {
            if (string.IsNullOrEmpty(_accessToken) || string.IsNullOrEmpty(_clientID))
            {
                Debug.WriteLine("Access token or ClientID is null; cannot initialize.");
                _authState.Value = (float)AuthState.NotAuthenticated;
                return false;
            }

            // Check absolute expiry (log only, handled by isNearExpiry in Initialize)
            if (DateTime.UtcNow >= _tokenExpiration)
            {
                Debug.WriteLine($"Access token fully expired (Expiration UTC: {_tokenExpiration.ToString("o")}, Kind: {_tokenExpiration.Kind}, Now UTC: {DateTime.UtcNow.ToString("o")}, Kind: {DateTime.UtcNow.Kind}); refresh required.");
            }

            try
            {
                Debug.WriteLine("Initializing client with Access Token...");
                var config = SpotifyClientConfig.CreateDefault().WithToken(_accessToken, "Bearer");
                _spotifyClient = new SpotifyClient(config);
                Debug.WriteLine("Initialized Spotify client with stored access token.");
                _authState.Value = (float)AuthState.Authenticated;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize client with stored token: {ex.Message}");
                _spotifyClient = null;
                _authState.Value = (float)AuthState.Error;
                return false;
            }
        }

        // Attempts to refresh the Spotify access token using the stored refresh token
        private async Task<bool> TryRefreshTokenAsync()
        {
            if (_refreshFailed || _refreshToken == null || _clientID == null)
            {
                if (!_refreshFailed) Debug.WriteLine("Refresh token or ClientID missing; cannot refresh.");
                _authState.Value = (float)AuthState.NotAuthenticated;
                return false;
            }

            try
            {
                Debug.WriteLine($"Attempting token refresh with Spotify API... (forceInvalidGrant: {_forceInvalidGrant})");
                Debug.WriteLine($"Current token expiration UTC: {_tokenExpiration.ToString("o")}");
                var response = await new OAuthClient().RequestToken(
                    new PKCETokenRefreshRequest(_clientID, _refreshToken)
                );

                if (_forceInvalidGrant)
                {
                    await Task.Delay(1000); // Short delay to simulate network
                    throw new APIException("Simulated invalid_grant for testing", null!);
                }

                var authenticator = new PKCEAuthenticator(_clientID, response);
                var config = SpotifyClientConfig.CreateDefault().WithAuthenticator(authenticator);
                _spotifyClient = new SpotifyClient(config);

                _accessToken = response.AccessToken;
                _refreshToken = response.RefreshToken ?? _refreshToken; // Update if provided
                _tokenExpiration = DateTime.UtcNow.AddSeconds(response.ExpiresIn);
                SaveTokens(_accessToken, _tokenExpiration);

                Debug.WriteLine($"Successfully refreshed token; new expiry UTC: {_tokenExpiration.ToString("o")}");
                _authState.Value = (float)AuthState.Authenticated;
                return true;
            }
            catch (APIException apiEx) when (apiEx.Message.Contains("invalid_grant"))
            {
                Debug.WriteLine($"Error refreshing token: invalid_grant - refresh token likely revoked by Spotify after prolonged inactivity or debug simulation (forceInvalidGrant: {_forceInvalidGrant}).");
                HandleError("Error refreshing token - refresh token invalid, please reauthorize");
                _refreshToken = null;
                _accessToken = null;
                _refreshFailed = true; // Mark as failed to stop spam
                _authState.Value = (float)AuthState.NotAuthenticated; // Set to NotAuthenticated for reauth prompt
                try
                {
                    if (File.Exists(_tokenFilePath))
                    {
                        Debug.WriteLine($"Attempting to delete token file '{_tokenFilePath}' due to invalid_grant...");
                        File.Delete(_tokenFilePath);
                        Debug.WriteLine($"Deleted token file '{_tokenFilePath}' successfully.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to delete token file: {ex.Message}");
                }
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error refreshing token: {ex.Message}");
                HandleError("Error refreshing token");
                _refreshToken = null;
                _accessToken = null;
                _refreshFailed = true; // Mark as failed to stop spam
                _authState.Value = (float)AuthState.Error;
                return false;
            }
        }

        // Saves access token and expiration to spotifyrefresh.tmp file
        private void SaveTokens(string accessToken, DateTime expiration)
        {
            try
            {
                Debug.WriteLine($"Saving tokens - AccessToken: {accessToken.Substring(0, 10)}..., RefreshToken: {(string.IsNullOrEmpty(_refreshToken) ? "null" : "set")}, Expiration UTC: {expiration.ToString("o")}");
                var parser = new FileIniDataParser();
                IniData tokenConfig = new IniData();

                tokenConfig["Spotify Tokens"]["RefreshToken"] = _refreshToken ?? "";
                tokenConfig["Spotify Tokens"]["AccessToken"] = accessToken;
                tokenConfig["Spotify Tokens"]["TokenExpiration"] = expiration.ToString("o");

                parser.WriteFile(_tokenFilePath, tokenConfig);
                if (File.Exists(_tokenFilePath))
                {
                    Debug.WriteLine($"Tokens saved to spotifyrefresh.tmp successfully; file last modified UTC: {File.GetLastWriteTimeUtc(_tokenFilePath).ToString("o")}");
                }
                else
                {
                    Debug.WriteLine($"Failed to verify token file existence after save: {_tokenFilePath}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving tokens to spotifyrefresh.tmp: {ex.Message}; StackTrace: {ex.StackTrace}");
                HandleError($"Error saving tokens: {ex.Message}");
                _authState.Value = (float)AuthState.Error;
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
                    _authState.Value = (float)AuthState.Error;
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
                _authState.Value = (float)AuthState.Authenticating;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error starting authentication: {ex.Message}");
                HandleError($"Error starting authentication: {ex.Message}");
                _authState.Value = (float)AuthState.Error;
            }
        }

        // Handles the OAuth callback, exchanging the code for tokens
        private async Task OnAuthorizationCodeReceived(object sender, AuthorizationCodeResponse response)
        {
            if (_verifier == null || _clientID == null)
            {
                HandleError("Authentication setup error");
                _authState.Value = (float)AuthState.Error;
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

                _accessToken = initialResponse.AccessToken;
                _tokenExpiration = DateTime.UtcNow.AddSeconds(initialResponse.ExpiresIn);
                SaveTokens(_accessToken, _tokenExpiration);

                await _server.Stop();
                _server = null; // Ensure reentrant Close() safety
                Debug.WriteLine("Authentication completed successfully.");
                _authState.Value = (float)AuthState.Authenticated;

                StartBackgroundTokenRefresh();
            }
            catch (APIException apiEx)
            {
                HandleError("API authentication error");
                if (apiEx.Response != null && Debugger.IsAttached)
                {
                    Debug.WriteLine($"API Response Error: {apiEx.Message}");
                }
                _authState.Value = (float)AuthState.Error;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Authentication failed: {ex.Message}");
                HandleError($"Authentication failed: {ex.Message}");
                _authState.Value = (float)AuthState.Error;
            }
        }

        // Cleans up resources when the plugin is closed (reentrant)
        public override void Close()
        {
            Debug.WriteLine($"Close called at UTC: {DateTime.UtcNow.ToString("o")}; TokenExpiration UTC: {_tokenExpiration.ToString("o")}, AuthState: {_authState.Value}");
            if (!_refreshCancellationTokenSource.IsCancellationRequested)
            {
                _refreshCancellationTokenSource.Cancel();
            }
            if (_server != null)
            {
                _server.Dispose();
                _server = null;
            }
            _spotifyClient = null; // Reset for reentrancy
            _authState.Value = (float)AuthState.NotAuthenticated;
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
            if (_refreshFailed)
            {
                Debug.WriteLine("Skipping update due to previous refresh failure.");
                _currentTrack.Value = "Reauthorize Required"; // Hint to user
                return;
            }
            await GetSpotifyInfo();
        }

        // Fetches and updates track info, using caching to optimize API calls
        private async Task GetSpotifyInfo()
        {
            Debug.WriteLine($"GetSpotifyInfo called at UTC: {DateTime.UtcNow.ToString("o")}");
            Debug.WriteLine($"SpotifyClient null? {_spotifyClient == null}");

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

            if (timeSinceLastCall < UpdateInterval.TotalSeconds && !forceSync && _lastTrackId != null && _isPlaying && !_pauseDetected)
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
                Debug.WriteLine("Fetching current playback from Spotify API...");
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
                        _currentTrack.Value = "Resuming Playback...";
                        _artist.Value = "Resuming Playback...";
                        _album.Value = "Resuming Playback...";
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

                        if (_isResuming)
                        {
                            _isResuming = false; // Reset on first successful API call
                            _currentTrack.Value = _displayTrackName;
                            _artist.Value = _displayArtistName;
                            _album.Value = _displayAlbumName;
                        }
                        else
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
                }
            }
            catch (AggregateException aggEx) when (aggEx.InnerExceptions.Any(e => e is APIException apiEx && apiEx.Message.Contains("expired")))
            {
                Debug.WriteLine($"Token expired during playback fetch at UTC: {DateTime.UtcNow.ToString("o")}; attempting refresh...");
                if (await TryRefreshTokenAsync())
                {
                    Debug.WriteLine("Token refreshed successfully during playback fetch; retrying...");
                    await GetSpotifyInfo(); // Retry playback fetch with new token
                }
                else
                {
                    Debug.WriteLine("Failed to refresh token during playback fetch.");
                    HandleError("Error updating Spotify info - token refresh failed");
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
            const int maxDelaySeconds = 10;
            Exception? lastException = null;

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
                    lastException = apiEx;
                    if (attempts < maxAttempts)
                    {
                        Debug.WriteLine($"Rate limit hit, waiting {delay.TotalSeconds}s. Attempt {attempts}/{maxAttempts}");
                        await Task.Delay(delay);
                        delay = TimeSpan.FromSeconds(Math.Min(maxDelaySeconds, (int)delay.TotalSeconds * 2 + new Random().Next(1, 3)));
                    }
                }
                catch (APIException apiEx)
                {
                    attempts++;
                    lastException = apiEx;
                    if (attempts < maxAttempts)
                    {
                        Debug.WriteLine($"API Error: {apiEx.Message}. Retrying after 1s. Attempt {attempts}/{maxAttempts}");
                        await Task.Delay(TimeSpan.FromSeconds(1));
                        delay = TimeSpan.FromSeconds((int)delay.TotalSeconds + new Random().Next(1, 2));
                    }
                }
                catch (HttpRequestException httpEx)
                {
                    attempts++;
                    lastException = httpEx;
                    if (attempts < maxAttempts)
                    {
                        Debug.WriteLine($"HTTP Error: {httpEx.Message}. Retrying after 1s. Attempt {attempts}/{maxAttempts}");
                        await Task.Delay(TimeSpan.FromSeconds(1));
                        delay = TimeSpan.FromSeconds((int)delay.TotalSeconds + new Random().Next(1, 2));
                    }
                }
            }

            Debug.WriteLine($"All {maxAttempts} retry attempts failed: {lastException?.Message}");
            throw new AggregateException($"All {maxAttempts} retry attempts failed.", lastException!);
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

        // Logs errors and updates UI with error message
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

        // Thread-safe via ConcurrentQueue; locking not added as updates are single-threaded in InfoPanel context
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
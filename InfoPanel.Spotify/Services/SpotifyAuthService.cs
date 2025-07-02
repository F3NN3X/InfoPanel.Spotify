using System.Diagnostics;
using System.Globalization;
using InfoPanel.Spotify.Models;
using IniParser;
using IniParser.Model;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Auth;

namespace InfoPanel.Spotify.Services;

/// <summary>
/// Handles Spotify API authentication, token management, and refresh operations.
/// </summary>
public sealed class SpotifyAuthService
{
    private readonly string _clientID;
    private readonly string _tokenFilePath;
    private SpotifyClient? _spotifyClient;
    private string? _verifier;
    private EmbedIOAuthServer? _server;
    private string? _refreshToken;
    private string? _accessToken;
    private DateTime _tokenExpiration;
    private bool _refreshFailed;
    private bool _forceInvalidGrant;

    // Constants for token management
    public const int TokenExpirationBufferSeconds = 60;
    public const int TokenRefreshCheckIntervalSeconds = 60;
    public const int TokenRefreshMaxRetries = 3;
    public const int TokenRefreshRetryDelaySeconds = 5;
    public const int TokenNearExpiryThresholdSeconds = 300; // 5 minutes

    // Events for auth state changes
    public event EventHandler<AuthState>? AuthStateChanged;
    public event EventHandler<SpotifyClient>? ClientInitialized;

    public SpotifyClient? Client => _spotifyClient;
    public bool RefreshFailed => _refreshFailed;
    public DateTime TokenExpiration => _tokenExpiration;
    public string? RefreshToken => _refreshToken;
    public string? AccessToken => _accessToken;

    public SpotifyAuthService(string clientID, string tokenFilePath)
    {
        _clientID = clientID;
        _tokenFilePath = tokenFilePath;
        _refreshFailed = false;

#if DEBUG
        _forceInvalidGrant = false; // Default, overridden by .ini in debug
#else
        _forceInvalidGrant = false; // Hardcoded false in release
#endif
        LoadTokens();
    }

    /// <summary>
    /// Sets the force invalid grant flag (for testing purposes).
    /// </summary>
    public void SetForceInvalidGrant(bool value)
    {
#if DEBUG
        _forceInvalidGrant = value;
#endif
    }

    /// <summary>
    /// Loads saved tokens from file.
    /// </summary>
    public bool LoadTokens()
    {
        if (!File.Exists(_tokenFilePath))
        {
            Debug.WriteLine("No spotifyrefresh.tmp found; will create on first authentication via button.");
            return false;
        }

        try
        {
            Debug.WriteLine($"Token file last modified UTC: {File.GetLastWriteTimeUtc(_tokenFilePath).ToString("o")}");
            using var fileStream = new FileStream(_tokenFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(fileStream);

            string fileContent = reader.ReadToEnd();
            Debug.WriteLine($"Raw .tmp content: {fileContent}");

            var parser = new FileIniDataParser();
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
                ResetTokens();
                return false;
            }

            Debug.WriteLine($"Loaded tokens from spotifyrefresh.tmp - Refresh Token: {(string.IsNullOrEmpty(_refreshToken) ? "null" : "set")}, Access Token: {(string.IsNullOrEmpty(_accessToken) ? "null" : "set")}, Expiration UTC: {_tokenExpiration.ToString("o")}, Kind: {_tokenExpiration.Kind}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error reading token file: {ex.Message}");
            ResetTokens();
            return false;
        }
    }

    private void ResetTokens()
    {
        _refreshToken = null;
        _accessToken = null;
        _tokenExpiration = DateTime.MinValue;
    }

    /// <summary>
    /// Saves access token and expiration to the token file.
    /// </summary>
    public void SaveTokens(string accessToken, DateTime expiration)
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
            OnAuthStateChanged(AuthState.Error);
        }
    }

    /// <summary>
    /// Attempts to initialize the SpotifyClient with stored access token.
    /// </summary>
    public bool TryInitializeClientWithAccessToken()
    {
        if (string.IsNullOrEmpty(_accessToken) || string.IsNullOrEmpty(_clientID))
        {
            Debug.WriteLine("Access token or ClientID is null; cannot initialize.");
            OnAuthStateChanged(AuthState.NotAuthenticated);
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
            OnAuthStateChanged(AuthState.Authenticated);
            OnClientInitialized(_spotifyClient);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to initialize client with stored token: {ex.Message}");
            _spotifyClient = null;
            OnAuthStateChanged(AuthState.Error);
            return false;
        }
    }

    /// <summary>
    /// Starts the PKCE authentication process.
    /// </summary>
    public void StartAuthentication()
    {
        try
        {
            var (verifier, challenge) = PKCEUtil.GenerateCodes();
            _verifier = verifier;
            _refreshFailed = false; // Reset on manual auth

            _server = new EmbedIOAuthServer(new Uri("http://127.0.0.1:5000/callback"), 5000);
            _server.AuthorizationCodeReceived += OnAuthorizationCodeReceived;
            _server.Start();

            if (_clientID == null)
            {
                OnAuthStateChanged(AuthState.Error);
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
                Scope = ["user-read-playback-state", "user-read-currently-playing"],
            };
            var uri = loginRequest.ToUri();

            Debug.WriteLine($"Authentication URI: {uri}");
            Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true });
            Debug.WriteLine("Authentication process started.");
            OnAuthStateChanged(AuthState.Authenticating);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error starting authentication: {ex.Message}");
            OnAuthStateChanged(AuthState.Error);
        }
    }

    /// <summary>
    /// Handles the OAuth callback.
    /// </summary>
    private async Task OnAuthorizationCodeReceived(object sender, AuthorizationCodeResponse response)
    {
        if (_verifier == null || _clientID == null)
        {
            OnAuthStateChanged(AuthState.Error);
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
            OnAuthStateChanged(AuthState.Authenticated);
            OnClientInitialized(_spotifyClient);
        }
        catch (APIException apiEx)
        {
            if (apiEx.Response != null && Debugger.IsAttached)
            {
                Debug.WriteLine($"API Response Error: {apiEx.Message}");
            }
            OnAuthStateChanged(AuthState.Error);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Authentication failed: {ex.Message}");
            OnAuthStateChanged(AuthState.Error);
        }
    }

    /// <summary>
    /// Attempts to refresh the access token.
    /// </summary>
    public async Task<bool> TryRefreshTokenAsync()
    {
        if (_refreshFailed || _refreshToken == null || _clientID == null)
        {
            if (!_refreshFailed)
            {
                Debug.WriteLine("Refresh token or ClientID missing; cannot refresh.");
            }

            OnAuthStateChanged(AuthState.NotAuthenticated);
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
            OnAuthStateChanged(AuthState.Authenticated);
            OnClientInitialized(_spotifyClient);
            return true;
        }
        catch (APIException apiEx) when (apiEx.Message.Contains("invalid_grant"))
        {
            Debug.WriteLine($"Error refreshing token: invalid_grant - refresh token likely revoked by Spotify after prolonged inactivity or debug simulation (forceInvalidGrant: {_forceInvalidGrant}).");
            _refreshToken = null;
            _accessToken = null;
            _refreshFailed = true; // Mark as failed to stop spam
            OnAuthStateChanged(AuthState.NotAuthenticated); // Set to NotAuthenticated for reauth prompt
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
            _refreshToken = null;
            _accessToken = null;
            _refreshFailed = true; // Mark as failed to stop spam
            OnAuthStateChanged(AuthState.Error);
            return false;
        }
    }

    /// <summary>
    /// Clean-up resources on close.
    /// </summary>
    public void Close()
    {
        if (_server != null)
        {
            _server.Dispose();
            _server = null;
        }
        _spotifyClient = null;
    }

    /// <summary>
    /// Raises the AuthStateChanged event.
    /// </summary>
    private void OnAuthStateChanged(AuthState state) =>
        AuthStateChanged?.Invoke(this, state);

    /// <summary>
    /// Raises the ClientInitialized event.
    /// </summary>
    private void OnClientInitialized(SpotifyClient client) =>
        ClientInitialized?.Invoke(this, client);
}
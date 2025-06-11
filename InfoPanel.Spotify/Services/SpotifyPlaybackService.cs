using System.Diagnostics;
using System.Net;
using SpotifyAPI.Web;

namespace InfoPanel.Spotify.Services;

/// <summary>
/// Handles Spotify playback information and API requests.
/// </summary>
public sealed class SpotifyPlaybackService
{
    private readonly RateLimiter _rateLimiter;
    private SpotifyClient? _spotifyClient;

    // Playback state tracking
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

    // Track information caching
    private string? _lastTrackName;
    private string? _lastArtistName;
    private string? _lastAlbumName;
    private string? _lastCoverUrl;

    // Constants for timing and detection thresholds
    private const int ProgressToleranceMs = 1500;
    private const int PauseThreshold = 2;

    // Events for playback updates
    public event EventHandler<PlaybackInfo>? PlaybackUpdated;
    public event EventHandler<string>? PlaybackError;

    public SpotifyPlaybackService(RateLimiter rateLimiter)
    {
        _rateLimiter = rateLimiter;
    }

    /// <summary>
    /// Sets the Spotify client to use for API requests.
    /// </summary>
    public void SetClient(SpotifyClient client) =>
        _spotifyClient = client;

    /// <summary>
    /// Gets the current playback state from Spotify API or estimates it based on elapsed time.
    /// </summary>
    public async Task<PlaybackInfo?> GetCurrentPlaybackAsync(bool forceApiRefresh = false)
    {
        if (_spotifyClient == null)
        {
            Debug.WriteLine("Spotify client is not initialized.");
            OnPlaybackError("Spotify client not initialized");
            return null;
        }

        var now = DateTime.UtcNow;
        var timeSinceLastCall = (now - _lastApiCallTime).TotalSeconds;
        bool forceSync = forceApiRefresh;

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

        // If we can use cached data, calculate estimated position
        if (timeSinceLastCall < 1.0 && !forceSync && _lastTrackId != null && _isPlaying && !_pauseDetected)
        {
            int elapsedMs = _lastProgressMs + (int)(timeSinceLastCall * 1000);
            if (elapsedMs >= _lastDurationMs)
            {
                _isPlaying = false;
                _trackEnded = true;
                _trackEndTime = DateTime.UtcNow;

                var trackEndedInfo = new PlaybackInfo(
                    TrackName: "Track Ended",
                    ArtistName: _lastArtistName ?? "Unknown",
                    AlbumName: _lastAlbumName ?? "Unknown",
                    CoverUrl: _lastCoverUrl,
                    ProgressMs: _lastDurationMs,
                    DurationMs: _lastDurationMs,
                    IsPlaying: false,
                    TrackId: _lastTrackId
                );

                OnPlaybackUpdated(trackEndedInfo);
                return trackEndedInfo;
            }

            var estimatedInfo = new PlaybackInfo(
                TrackName: _lastTrackName,
                ArtistName: _lastArtistName,
                AlbumName: _lastAlbumName,
                CoverUrl: _lastCoverUrl,
                ProgressMs: elapsedMs,
                DurationMs: _lastDurationMs,
                IsPlaying: _isPlaying,
                TrackId: _lastTrackId
            );

            Debug.WriteLine($"Estimated - Elapsed: {TimeSpan.FromMilliseconds(elapsedMs).ToString(@"mm\:ss")}, Remaining: {TimeSpan.FromMilliseconds(_lastDurationMs - elapsedMs).ToString(@"mm\:ss")}, Progress: {(_lastDurationMs > 0 ? elapsedMs / (double)_lastDurationMs * 100 : 0):F1}%");
            OnPlaybackUpdated(estimatedInfo);
            return estimatedInfo;
        }

        if (!_rateLimiter.TryRequest())
        {
            Debug.WriteLine("Rate limit exceeded, waiting...");
            await Task.Delay(1000);
            OnPlaybackError("Rate limit exceeded");
            return null;
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

                    var pausedInfo = new PlaybackInfo(
                        TrackName: "Paused",
                        ArtistName: "Paused",
                        AlbumName: "Paused",
                        CoverUrl: _lastCoverUrl,
                        ProgressMs: playback.ProgressMs,
                        DurationMs: result.DurationMs,
                        IsPlaying: false,
                        TrackId: result.Id
                    );

                    OnPlaybackUpdated(pausedInfo);
                    return pausedInfo;
                }
                else if (_isPlaying && _previousProgressMs >= 0 && Math.Abs(playback.ProgressMs - _previousProgressMs) <= ProgressToleranceMs)
                {
                    _pauseCount++;
                    if (_pauseCount >= PauseThreshold && !_pauseDetected)
                    {
                        Debug.WriteLine("Progress stalled (pause detected), forcing API sync and stopping estimation.");
                        _isPlaying = false;
                        _pauseDetected = true;

                        var stalledInfo = new PlaybackInfo(
                            TrackName: "Paused",
                            ArtistName: "Paused",
                            AlbumName: "Paused",
                            CoverUrl: _lastCoverUrl,
                            ProgressMs: playback.ProgressMs,
                            DurationMs: result.DurationMs,
                            IsPlaying: false,
                            TrackId: result.Id
                        );

                        OnPlaybackUpdated(stalledInfo);
                        return stalledInfo;
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

                    var resumingInfo = new PlaybackInfo(
                        TrackName: "Resuming Playback...",
                        ArtistName: "Resuming Playback...",
                        AlbumName: "Resuming Playback...",
                        CoverUrl: _lastCoverUrl,
                        ProgressMs: playback.ProgressMs,
                        DurationMs: result.DurationMs,
                        IsPlaying: true,
                        TrackId: result.Id
                    );

                    OnPlaybackUpdated(resumingInfo);
                    return resumingInfo;
                }

                if (_isPlaying || _lastTrackId != result.Id)
                {
                    _pauseDetected = false;
                    _trackEnded = false;
                    _lastTrackName = !string.IsNullOrEmpty(result.Name) ? result.Name : "Unknown Track";
                    _lastArtistName = string.Join(", ", result.Artists.Select(a => a.Name ?? "Unknown"));
                    _lastAlbumName = !string.IsNullOrEmpty(result.Album.Name) ? result.Album.Name : "Unknown Album";
                    _lastCoverUrl = result.Album.Images.FirstOrDefault()?.Url ?? string.Empty;

                    var playingInfo = new PlaybackInfo(
                        TrackName: _lastTrackName,
                        ArtistName: _lastArtistName,
                        AlbumName: _lastAlbumName,
                        CoverUrl: _lastCoverUrl,
                        ProgressMs: _lastProgressMs,
                        DurationMs: _lastDurationMs,
                        IsPlaying: _isPlaying,
                        TrackId: _lastTrackId
                    );

                    if (_isResuming)
                    {
                        _isResuming = false; // Reset on first successful API call
                    }

                    Debug.WriteLine($"Synced - Track: {_lastTrackName}, Artist: {_lastArtistName}, Album: {_lastAlbumName}, Cover URL: {_lastCoverUrl}");
                    Debug.WriteLine($"Elapsed: {TimeSpan.FromMilliseconds(_lastProgressMs).ToString(@"mm\:ss")}, Remaining: {TimeSpan.FromMilliseconds(_lastDurationMs - _lastProgressMs).ToString(@"mm\:ss")}, Progress: {(_lastDurationMs > 0 ? _lastProgressMs / (double)_lastDurationMs * 100 : 0):F1}%");

                    OnPlaybackUpdated(playingInfo);
                    return playingInfo;
                }
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
                    var trackEndedInfo = new PlaybackInfo(
                        TrackName: "Track Ended",
                        ArtistName: _lastArtistName ?? "Unknown",
                        AlbumName: _lastAlbumName ?? "Unknown",
                        CoverUrl: _lastCoverUrl,
                        ProgressMs: _lastDurationMs,
                        DurationMs: _lastDurationMs,
                        IsPlaying: false,
                        TrackId: null
                    );

                    OnPlaybackUpdated(trackEndedInfo);
                    return trackEndedInfo;
                }
                else
                {
                    _trackEnded = false;
                    _previousProgressMs = -1;
                    _lastTrackName = null;
                    _lastArtistName = null;
                    _lastAlbumName = null;
                    _lastCoverUrl = null;

                    var noTrackInfo = new PlaybackInfo(
                        TrackName: "No track playing",
                        ArtistName: "No track playing",
                        AlbumName: "No track playing",
                        CoverUrl: null,
                        ProgressMs: 0,
                        DurationMs: 0,
                        IsPlaying: false,
                        TrackId: null
                    );

                    OnPlaybackUpdated(noTrackInfo);
                    return noTrackInfo;
                }
            }
        }
        catch (AggregateException aggEx) when (aggEx.InnerExceptions.Any(e => e is APIException apiEx && apiEx.Message.Contains("expired")))
        {
            Debug.WriteLine($"Token expired during playback fetch at UTC: {DateTime.UtcNow.ToString("o")}");
            OnPlaybackError("Token expired");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error fetching Spotify playback: {ex.Message}");
            OnPlaybackError("Error updating Spotify info");
            return null;
        }

        return null;
    }

    /// <summary>
    /// Executes an API call with retry logic for rate limits or network errors.
    /// </summary>
    private async Task<T?> ExecuteWithRetry<T>(Func<Task<T>> operation, int maxAttempts = 3)
    {
        int attempts = 0;
        TimeSpan delay = TimeSpan.FromSeconds(1);
        const int MaxDelaySeconds = 10;
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
                    delay = TimeSpan.FromSeconds(Math.Min(seconds, MaxDelaySeconds));
                }
                else
                {
                    delay = TimeSpan.FromSeconds(Math.Min(5, MaxDelaySeconds));
                }

                attempts++;
                lastException = apiEx;
                if (attempts < maxAttempts)
                {
                    Debug.WriteLine($"Rate limit hit, waiting {delay.TotalSeconds}s. Attempt {attempts}/{maxAttempts}");
                    await Task.Delay(delay);
                    delay = TimeSpan.FromSeconds(Math.Min(MaxDelaySeconds, (int)delay.TotalSeconds * 2 + new Random().Next(1, 3)));
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

    /// <summary>
    /// Raises the PlaybackUpdated event.
    /// </summary>
    private void OnPlaybackUpdated(PlaybackInfo info) =>
        PlaybackUpdated?.Invoke(this, info);

    /// <summary>
    /// Raises the PlaybackError event.
    /// </summary>
    private void OnPlaybackError(string errorMessage) =>
        PlaybackError?.Invoke(this, errorMessage);

    /// <summary>
    /// Resets cached playback state.
    /// </summary>
    public void Reset()
    {
        _lastTrackId = null;
        _lastProgressMs = 0;
        _previousProgressMs = -1;
        _lastDurationMs = 0;
        _isPlaying = false;
        _pauseDetected = false;
        _pauseCount = 0;
        _trackEnded = false;
        _isResuming = false;
        _lastTrackName = null;
        _lastArtistName = null;
        _lastAlbumName = null;
        _lastCoverUrl = null;
    }
}

/// <summary>
/// Contains information about current Spotify playback.
/// </summary>
public sealed record PlaybackInfo(
    string? TrackName,
    string? ArtistName,
    string? AlbumName,
    string? CoverUrl,
    int ProgressMs,
    int DurationMs,
    bool IsPlaying,
    string? TrackId
);
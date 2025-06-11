using System.Collections.Concurrent;

namespace InfoPanel.Spotify.Services;

/// <summary>
/// Implements rate limiting for API requests to avoid hitting Spotify API limits.
/// Handles both per-minute and per-second rate limits.
/// </summary>
public sealed class RateLimiter
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
        CleanupExpiredRequests(_requestTimesMinute, _minuteWindow, now);
        if (_requestTimesMinute.Count > _maxRequestsPerMinute)
        {
            return false;
        }

        _requestTimesSecond.Enqueue(now);
        CleanupExpiredRequests(_requestTimesSecond, _secondWindow, now);
        if (_requestTimesSecond.Count > _maxRequestsPerSecond)
        {
            return false;
        }

        return true;
    }

    private static void CleanupExpiredRequests(ConcurrentQueue<DateTime> queue, TimeSpan window, DateTime now)
    {
        while (queue.TryPeek(out DateTime oldest) && (now - oldest) > window)
        {
            queue.TryDequeue(out _);
        }
    }
}
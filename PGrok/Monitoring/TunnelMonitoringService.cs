using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PGrok.Monitoring;

/// <summary>
/// Provides monitoring and diagnostics services for the PGrok tunnel server
/// </summary>
public class TunnelMonitoringService
{
    private readonly ILogger _logger;
    private readonly Meter _meter;
    private readonly Counter<long> _requestCounter;
    private readonly Counter<long> _errorCounter;
    private readonly Histogram<double> _requestDuration;
    private readonly Counter<long> _bytesTransferred;
    private readonly ConcurrentDictionary<string, TunnelStats> _tunnelStats = new();
    private readonly ConcurrentDictionary<string, EndpointStats> _endpointStats = new();
    private readonly TunnelMonitoringConfig _config;

    // Track totals locally for direct access
    private long _totalRequests;
    private long _totalErrors;
    private long _totalBytes;

    public TunnelMonitoringService(ILogger logger, TunnelMonitoringConfig config)
    {
        _logger = logger;
        _config = config;

        // Create metrics
        _meter = new Meter("PGrok.Tunnel");

        _requestCounter = _meter.CreateCounter<long>(
            "pgrok.requests.total",
            "Count",
            "Total number of requests processed");

        _errorCounter = _meter.CreateCounter<long>(
            "pgrok.errors.total",
            "Count",
            "Total number of errors encountered");

        _requestDuration = _meter.CreateHistogram<double>(
            "pgrok.request.duration",
            "ms",
            "Request processing duration");

        _bytesTransferred = _meter.CreateCounter<long>(
            "pgrok.bytes.transferred",
            "Bytes",
            "Total bytes transferred");

        // Start periodic stats logging if enabled
        if (config.EnablePeriodicLogging)
        {
            StartPeriodicLogging(TimeSpan.FromSeconds(config.LoggingIntervalSeconds));
        }
    }

    /// <summary>
    /// Records metrics for a tunnel request
    /// </summary>
    public void RecordRequest(string tunnelId, string endpoint, int statusCode, long bytes, TimeSpan duration, bool isWebSocket = false)
    {
        // Update global counters
        _requestCounter.Add(1);
        Interlocked.Increment(ref _totalRequests);

        _bytesTransferred.Add(bytes);
        Interlocked.Add(ref _totalBytes, bytes);

        _requestDuration.Record(duration.TotalMilliseconds);

        if (statusCode >= 400)
        {
            _errorCounter.Add(1);
            Interlocked.Increment(ref _totalErrors);
        }

        // Update tunnel-specific stats
        var stats = _tunnelStats.GetOrAdd(tunnelId, id => new TunnelStats(id));
        stats.RecordRequest(statusCode, bytes, duration, isWebSocket);

        // Update endpoint stats
        var epStats = _endpointStats.GetOrAdd(endpoint, ep => new EndpointStats(ep));
        epStats.RecordRequest(statusCode, duration);

        // Log if slow request
        if (duration > TimeSpan.FromSeconds(_config.SlowRequestThresholdSeconds))
        {
            _logger.LogWarning($"Slow request: {endpoint} ({duration.TotalMilliseconds:F2}ms)");
        }
    }

    /// <summary>
    /// Records a tunnel connection event
    /// </summary>
    public void RecordTunnelConnection(string tunnelId, bool isConnected)
    {
        var stats = _tunnelStats.GetOrAdd(tunnelId, id => new TunnelStats(id));

        if (isConnected)
        {
            stats.RecordConnection();
        }
        else
        {
            stats.RecordDisconnection();
        }

        _logger.LogInformation($"Tunnel {tunnelId} {(isConnected ? "connected" : "disconnected")}");
    }

    /// <summary>
    /// Records a WebSocket connection event
    /// </summary>
    public void RecordWebSocketConnection(string tunnelId, string connectionId, bool isConnected)
    {
        var stats = _tunnelStats.GetOrAdd(tunnelId, id => new TunnelStats(id));

        if (isConnected)
        {
            stats.RecordWebSocketConnection();
        }
        else
        {
            stats.RecordWebSocketDisconnection();
        }
    }

    /// <summary>
    /// Gets statistics for all tunnels
    /// </summary>
    public List<TunnelStats> GetAllTunnelStats()
    {
        return _tunnelStats.Values.ToList();
    }

    /// <summary>
    /// Gets statistics for a specific tunnel
    /// </summary>
    public TunnelStats? GetTunnelStats(string tunnelId)
    {
        return _tunnelStats.TryGetValue(tunnelId, out var stats) ? stats : null;
    }

    /// <summary>
    /// Gets statistics for all endpoints
    /// </summary>
    public List<EndpointStats> GetAllEndpointStats()
    {
        return _endpointStats.Values.ToList();
    }

    /// <summary>
    /// Writes tunnel statistics to the HTTP response
    /// </summary>
    public void WriteStatsResponse(HttpListenerResponse response, bool detailed = false)
    {
        var tunnels = GetAllTunnelStats();
        var endpoints = GetAllEndpointStats().OrderByDescending(e => e.RequestCount).Take(10).ToList();

        var stats = new {
            summary = new {
                totalRequests = _totalRequests,
                totalErrors = _totalErrors,
                totalBytes = _totalBytes,
                activeTunnels = tunnels.Count(t => t.IsActive),
                totalTunnels = tunnels.Count
            },
            tunnels = tunnels.Select(t => new {
                id = t.TunnelId,
                isActive = t.IsActive,
                requestCount = t.RequestCount,
                errorCount = t.ErrorCount,
                bytesTransferred = t.BytesTransferred,
                avgResponseTime = t.AverageResponseTime,
                lastActivity = t.LastActivity,
                webSocketCount = t.ActiveWebSockets,
                uptime = t.Uptime.ToString(@"dd\.hh\:mm\:ss")
            }),
            topEndpoints = endpoints.Select(e => new {
                path = e.Endpoint,
                requestCount = e.RequestCount,
                errorRate = e.ErrorRate,
                avgResponseTime = e.AverageResponseTime
            })
        };

        string json = JsonSerializer.Serialize(stats, new JsonSerializerOptions {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        byte[] buffer = Encoding.UTF8.GetBytes(json);

        response.ContentType = "application/json";
        response.ContentLength64 = buffer.Length;
        response.StatusCode = 200;

        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
    }

    /// <summary>
    /// Starts a background task to periodically log statistics
    /// </summary>
    private void StartPeriodicLogging(TimeSpan interval)
    {
        Task.Run(async () => {
            while (true)
            {
                await Task.Delay(interval);
                LogCurrentStats();
            }
        });
    }

    /// <summary>
    /// Logs current statistics
    /// </summary>
    private void LogCurrentStats()
    {
        var tunnels = GetAllTunnelStats();
        var activeTunnels = tunnels.Count(t => t.IsActive);

        _logger.LogInformation($"Stats: Requests={_totalRequests}, Errors={_totalErrors}, Active Tunnels={activeTunnels}");

        foreach (var tunnel in tunnels.Where(t => t.IsActive))
        {
            _logger.LogDebug($"Tunnel {tunnel.TunnelId}: Requests={tunnel.RequestCount}, Errors={tunnel.ErrorCount}, WebSockets={tunnel.ActiveWebSockets}");
        }
    }
}

/// <summary>
/// Statistics for a specific tunnel
/// </summary>
public class TunnelStats
{
    public string TunnelId { get; }
    public int RequestCount { get; private set; }
    public int ErrorCount { get; private set; }
    public long BytesTransferred { get; private set; }
    public int ConnectionCount { get; private set; }
    private int _ActiveWebSockets;
    public int ActiveWebSockets => _ActiveWebSockets;
    public DateTime CreatedAt { get; }
    public DateTime LastActivity { get; private set; }
    public DateTime? ConnectedAt { get; private set; }
    public DateTime? DisconnectedAt { get; private set; }
    public bool IsActive => ConnectedAt.HasValue && (!DisconnectedAt.HasValue || DisconnectedAt.Value < ConnectedAt.Value);
    public TimeSpan Uptime => IsActive && ConnectedAt.HasValue ? DateTime.UtcNow - ConnectedAt.Value : TimeSpan.Zero;

    private readonly List<double> _responseTimes = new();
    private readonly object _lock = new();

    public double AverageResponseTime
    {
        get
        {
            lock (_lock)
            {
                return _responseTimes.Count > 0 ? _responseTimes.Average() : 0;
            }
        }
    }

    public TunnelStats(string tunnelId)
    {
        TunnelId = tunnelId;
        CreatedAt = DateTime.UtcNow;
        LastActivity = DateTime.UtcNow;
    }

    public void RecordRequest(int statusCode, long bytes, TimeSpan duration, bool isWebSocket)
    {
        lock (_lock)
        {
            RequestCount++;
            BytesTransferred += bytes;
            LastActivity = DateTime.UtcNow;

            if (statusCode >= 400)
            {
                ErrorCount++;
            }

            _responseTimes.Add(duration.TotalMilliseconds);

            // Keep only the last 100 response times
            if (_responseTimes.Count > 100)
            {
                _responseTimes.RemoveAt(0);
            }
        }
    }

    public void RecordConnection()
    {
        ConnectionCount++;
        ConnectedAt = DateTime.UtcNow;
        LastActivity = DateTime.UtcNow;
    }

    public void RecordDisconnection()
    {
        DisconnectedAt = DateTime.UtcNow;
        LastActivity = DateTime.UtcNow;
    }

    public void RecordWebSocketConnection()
    {
        Interlocked.Increment(ref _ActiveWebSockets);
        LastActivity = DateTime.UtcNow;
    }

    public void RecordWebSocketDisconnection()
    {
        Interlocked.Decrement(ref _ActiveWebSockets);
        LastActivity = DateTime.UtcNow;
    }
}

/// <summary>
/// Statistics for a specific endpoint
/// </summary>
public class EndpointStats
{
    public string Endpoint { get; }
    public int RequestCount { get; private set; }
    public int ErrorCount { get; private set; }
    public double ErrorRate => RequestCount > 0 ? (double)ErrorCount / RequestCount : 0;
    public DateTime LastRequested { get; private set; }

    private readonly List<double> _responseTimes = new();
    private readonly object _lock = new();

    public double AverageResponseTime
    {
        get
        {
            lock (_lock)
            {
                return _responseTimes.Count > 0 ? _responseTimes.Average() : 0;
            }
        }
    }

    public EndpointStats(string endpoint)
    {
        Endpoint = endpoint;
        LastRequested = DateTime.UtcNow;
    }

    public void RecordRequest(int statusCode, TimeSpan duration)
    {
        lock (_lock)
        {
            RequestCount++;
            LastRequested = DateTime.UtcNow;

            if (statusCode >= 400)
            {
                ErrorCount++;
            }

            _responseTimes.Add(duration.TotalMilliseconds);

            // Keep only the last 100 response times
            if (_responseTimes.Count > 100)
            {
                _responseTimes.RemoveAt(0);
            }
        }
    }
}

/// <summary>
/// Configuration for the monitoring service
/// </summary>
public class TunnelMonitoringConfig
{
    public bool EnablePeriodicLogging { get; set; } = true;
    public int LoggingIntervalSeconds { get; set; } = 60;
    public int SlowRequestThresholdSeconds { get; set; } = 5;
    public bool EnableDetailedLogging { get; set; } = false;
}

/// <summary>
/// Extensions for HTTP monitoring and diagnostics
/// </summary>
public static class HttpMonitoringExtensions
{
    /// <summary>
    /// Records performance metrics for an HTTP request
    /// </summary>
    public static IDisposable TrackRequest(this HttpListenerContext context, TunnelMonitoringService monitoringService, string tunnelId)
    {
        return new RequestTracker(context, monitoringService, tunnelId);
    }

    /// <summary>
    /// Helper class to track request performance
    /// </summary>
    private class RequestTracker : IDisposable
    {
        private readonly HttpListenerContext _context;
        private readonly TunnelMonitoringService _monitoringService;
        private readonly string _tunnelId;
        private readonly Stopwatch _stopwatch;
        private readonly string _endpoint;
        private readonly bool _isWebSocket;

        public RequestTracker(HttpListenerContext context, TunnelMonitoringService monitoringService, string tunnelId)
        {
            _context = context;
            _monitoringService = monitoringService;
            _tunnelId = tunnelId;
            _stopwatch = Stopwatch.StartNew();
            _endpoint = context.Request.Url?.AbsolutePath ?? "/";
            _isWebSocket = context.Request.IsWebSocketRequest;
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            var duration = _stopwatch.Elapsed;
            var statusCode = _context.Response.StatusCode;
            var contentLength = _context.Response.ContentLength64;

            _monitoringService.RecordRequest(_tunnelId, _endpoint, statusCode, contentLength, duration, _isWebSocket);
        }
    }
}
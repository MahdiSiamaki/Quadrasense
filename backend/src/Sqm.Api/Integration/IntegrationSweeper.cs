using Microsoft.Extensions.Options;
using Sqm.Application.Integration;

namespace Sqm.Api.Integration;

/// <summary>
/// Housekeeping for the integration API (ADR-024): expired tokens deleted, and events older than the
/// retention pruned from the feed.
/// </summary>
/// <remarks>
/// A token is refused by its expiry whether or not it has been swept; sweeping keeps the table small. A
/// reader whose cursor is older than the oldest event kept is told so by the feed (<c>oldestAvailable</c>).
/// </remarks>
public sealed partial class IntegrationSweeper : BackgroundService
{
    [LoggerMessage(EventId = 1500, Level = LogLevel.Information,
        Message = "Integration housekeeping: {Tokens} expired token(s) and {Events} old event(s) removed")]
    private partial void LogSwept(int tokens, int events);

    [LoggerMessage(EventId = 1501, Level = LogLevel.Warning,
        Message = "Integration housekeeping failed; will retry on the next pass")]
    private partial void LogSweepFailed(Exception exception);

    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IIntegrationClientStore _clients;
    private readonly IIntegrationOutbox _outbox;
    private readonly IntegrationOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<IntegrationSweeper> _logger;

    /// <summary>Creates the sweeper.</summary>
    public IntegrationSweeper(
        IIntegrationClientStore clients, IIntegrationOutbox outbox, IOptions<IntegrationOptions> options, TimeProvider clock,
        ILogger<IntegrationSweeper> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _clients = clients;
        _outbox = outbox;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var tokens = await _clients.DeleteExpiredTokensAsync(stoppingToken).ConfigureAwait(false);
                var events = await _outbox.PruneAsync(
                    _clock.GetUtcNow().AddDays(-Math.Max(1, _options.OutboxRetentionDays)), stoppingToken).ConfigureAwait(false);
                if (tokens + events > 0)
                {
                    LogSwept(tokens, events);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Housekeeping must never take the API down. A database blip here costs one pass.
                LogSweepFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

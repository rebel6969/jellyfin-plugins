using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SimklMarkSync;

/// <summary>
/// Listens for manual played and unplayed marks and sends them to Simkl in batches.
/// </summary>
/// <remarks>
/// Jellyfin raises the event inside the request that saved the mark, so the handler only queues it. A single worker
/// waits until marks stop arriving for <see cref="QuietPeriod"/> (at most <see cref="MaxBatchDelay"/>), so marking a
/// whole season, which saves each episode separately, goes to Simkl as one request.
/// </remarks>
public sealed class MarkSyncService : BackgroundService
{
    /// <summary>
    /// How long marks must stop arriving before a batch is sent.
    /// </summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The longest a batch waits for marks to stop arriving.
    /// </summary>
    public static readonly TimeSpan MaxBatchDelay = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a batch collected when Jellyfin stops may take to send.
    /// </summary>
    public static readonly TimeSpan ShutdownSendTimeout = TimeSpan.FromSeconds(10);

    private readonly IUserDataManager _userDataManager;
    private readonly MarkSyncProcessor _processor;
    private readonly TimeProvider _time;
    private readonly ILogger<MarkSyncService> _logger;
    private readonly Channel<MarkChange> _marks = Channel.CreateUnbounded<MarkChange>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkSyncService"/> class.
    /// </summary>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="processor">The processor that sends a batch.</param>
    /// <param name="time">The time provider.</param>
    /// <param name="logger">The logger.</param>
    public MarkSyncService(IUserDataManager userDataManager, MarkSyncProcessor processor, TimeProvider time, ILogger<MarkSyncService> logger)
    {
        _userDataManager = userDataManager;
        _processor = processor;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Jellyfin creates plugin instances before it starts hosted services, so the official plugin is loaded by now.
        // This runs before the worker starts, so the credentials still have a single caller.
        _processor.LogReadiness();
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<MarkChange>();
        try
        {
            while (await _marks.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                var started = _time.GetTimestamp();
                Drain(batch);
                while (_time.GetElapsedTime(started) < MaxBatchDelay)
                {
                    await Task.Delay(QuietPeriod, _time, stoppingToken).ConfigureAwait(false);
                    if (!Drain(batch))
                    {
                        break;
                    }
                }

                await SendAsync(batch, stoppingToken).ConfigureAwait(false);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Jellyfin is stopping: send what was collected rather than drop it. Repeating part of a batch is safe.
            Drain(batch);
            if (batch.Count > 0)
            {
                using var timeout = new CancellationTokenSource(ShutdownSendTimeout, _time);
                try
                {
                    await SendAsync(batch, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Jellyfin stopped before {Count} marks reached Simkl", batch.Count);
                }
            }
        }
    }

    private bool Drain(List<MarkChange> batch)
    {
        var any = false;
        while (_marks.Reader.TryRead(out var mark))
        {
            batch.Add(mark);
            any = true;
        }

        return any;
    }

    private async Task SendAsync(List<MarkChange> batch, CancellationToken cancellationToken)
    {
        try
        {
            await _processor.ProcessAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Sending {Count} marks to Simkl failed: {Message}", batch.Count, ex.Message);
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            var mark = MarkChangeFactory.Create(e, static episode => episode.Series, _time.GetUtcNow().UtcDateTime, _logger);
            if (mark is not null)
            {
                _marks.Writer.TryWrite(mark);
            }
        }
        catch (Exception ex)
        {
            // This runs inside the request that saved the mark; an exception here would fail the user's mark.
            _logger.LogError(ex, "Could not queue a mark for Simkl: {Message}", ex.Message);
        }
    }
}

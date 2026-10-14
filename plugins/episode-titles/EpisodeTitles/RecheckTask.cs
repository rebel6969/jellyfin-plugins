using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
namespace Jellyfin.Plugin.EpisodeTitles;

/// <summary>
/// Jellyfin fetches an episode's metadata when the file arrives and not again, so a title that reaches TheTVDB or
/// AniDB later never replaces the placeholder. This refreshes placeholder-named episodes on <see cref="Schedule"/>;
/// <see cref="TitleFallbackProvider"/> then takes the first real title.
/// </summary>
public sealed class RecheckTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<RecheckTask> _logger;
    private readonly TimeProvider _time;

    public RecheckTask(
        ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, ILogger<RecheckTask> logger)
        : this(libraryManager, providerManager, fileSystem, logger, TimeProvider.System)
    {
    }

    internal RecheckTask(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<RecheckTask> logger,
        TimeProvider time)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
        _time = time;
    }

    public string Name => "Re-check placeholder episode titles";

    public string Key => "EpisodeTitlesRecheck";

    public string Description =>
        "Refreshes episodes still named \"Episode N\", \"TBA\" or after their file, so a title that reached a metadata " +
        "provider after import replaces the placeholder. New episodes are checked daily, older ones every seventh of " +
        "their age, at most every 30 days.";

    public string Category => "Episode Titles";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks }];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var due = _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                IsVirtualItem = false,
                Recursive = true,
            })
            .OfType<Episode>()
            .Where(e => !e.IsLocked
                && !e.LockedFields.Contains(MetadataField.Name)
                && Placeholder.IsPlaceholder(e.Name, e.Path)
                && Schedule.IsDue(now, e.PremiereDate, e.DateCreated, e.DateLastRefreshed))
            .ToList();
        _logger.LogInformation("EpisodeTitles: {Count} placeholder-named episodes are due for a re-check", due.Count);

        var renamed = 0;
        for (var i = 0; i < due.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var episode = due[i];
            var before = episode.Name;
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.Default,
            };
            try
            {
                await _providerManager.RefreshSingleItem(episode, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("EpisodeTitles: refreshing {Path} failed: {Message}", episode.Path, ex.Message);
            }

            if (!string.Equals(before, episode.Name, StringComparison.Ordinal))
            {
                renamed++;
            }

            progress.Report(100.0 * (i + 1) / due.Count);
        }

        _logger.LogInformation(
            "EpisodeTitles: re-checked {Count}, renamed {Renamed}, still a placeholder {Left}",
            due.Count, renamed, due.Count - renamed);
        progress.Report(100);
    }
}

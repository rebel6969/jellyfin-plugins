using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
namespace Jellyfin.Plugin.EpisodeTitles;

/// <summary>
/// Runs after Jellyfin has merged the remote providers' results into an episode. Jellyfin keeps the first non-empty
/// name in fetcher order, so a provider that answers with a stand-in ("Episode 3" from AniDB) hides a real title
/// from a later one (TheTVDB). When the merged name is a placeholder, this asks the same providers, in the same
/// order, and keeps the first real title.
/// </summary>
public sealed class TitleFallbackProvider : ICustomMetadataProvider<Episode>
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly ILogger<TitleFallbackProvider> _logger;

    public TitleFallbackProvider(
        ILibraryManager libraryManager, IProviderManager providerManager, ILogger<TitleFallbackProvider> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _logger = logger;
    }

    public string Name => "Episode Titles";

    public async Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        if (item.IsLocked || item.LockedFields.Contains(MetadataField.Name) || !Placeholder.IsPlaceholder(item.Name, item.Path))
        {
            return ItemUpdateType.None;
        }

        var providers = _providerManager.GetMetadataProviders<Episode>(item, _libraryManager.GetLibraryOptions(item))
            .OfType<IRemoteMetadataProvider<Episode, EpisodeInfo>>()
            .ToList();
        var lookup = item.GetLookupInfo();
        foreach (var provider in providers)
        {
            string? title;
            try
            {
                var result = await provider.GetMetadata(lookup, cancellationToken).ConfigureAwait(false);
                title = result.HasMetadata ? result.Item?.Name : null;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "EpisodeTitles: {Provider} failed for {Path}: {Message}", provider.Name, item.Path, ex.Message);
                continue;
            }

            if (Placeholder.IsPlaceholder(title, item.Path))
            {
                continue;
            }

            _logger.LogInformation(
                "EpisodeTitles: {Series} S{Season}E{Episode} was named {Old}, a placeholder; {Provider} has {New}",
                item.SeriesName, item.ParentIndexNumber, item.IndexNumber, item.Name, provider.Name, title.Trim());
            item.Name = title.Trim();
            return ItemUpdateType.MetadataEdit;
        }

        _logger.LogDebug(
            "EpisodeTitles: none of {Count} providers has a real title for {Path} yet", providers.Count, item.Path);
        return ItemUpdateType.None;
    }
}

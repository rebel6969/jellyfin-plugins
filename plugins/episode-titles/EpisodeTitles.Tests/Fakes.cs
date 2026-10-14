using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using Moq;
namespace Jellyfin.Plugin.EpisodeTitles.Tests;

/// <summary>A remote episode provider that answers from a script and records each lookup.</summary>
internal sealed class FakeProvider(string name, Func<EpisodeInfo, MetadataResult<Episode>> answer)
    : IRemoteMetadataProvider<Episode, EpisodeInfo>
{
    public FakeProvider(string name, string? title, bool hasMetadata = true)
        : this(name, _ => new MetadataResult<Episode> { HasMetadata = hasMetadata, Item = new Episode { Name = title! } })
    {
    }

    public string Name => name;

    public List<EpisodeInfo> Calls { get; } = [];

    public Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
    {
        Calls.Add(info);
        return Task.FromResult(answer(info));
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>
/// Episode.GetLookupInfo reads the library and server configuration through BaseItem's static services, as it does
/// inside Jellyfin. Every test installs the same stand-ins, so the order tests run in does not matter.
/// </summary>
internal static class JellyfinStatics
{
    private static readonly object Gate = new();

    internal static void Install()
    {
        lock (Gate)
        {
            if (BaseItem.LibraryManager is not null)
            {
                return;
            }

            var library = new Mock<ILibraryManager>();
            library.Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
            library.Setup(l => l.GetLibraryOptions(It.IsAny<BaseItem>()))
                .Returns(new MediaBrowser.Model.Configuration.LibraryOptions { PreferredMetadataLanguage = "en", MetadataCountryCode = "US" });
            var configuration = new Mock<IServerConfigurationManager>();
            configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());
            // Episode.IsMissingEpisode reads LocationType, which asks the file system whether the path is a file.
            var fileSystem = new Mock<IFileSystem>();
            fileSystem.Setup(f => f.IsPathFile(It.IsAny<string>())).Returns(true);
            BaseItem.FileSystem = fileSystem.Object;
            BaseItem.ConfigurationManager = configuration.Object;
            BaseItem.LibraryManager = library.Object;
        }
    }

    // BaseItem equality is by Id, so every test item needs its own.
    internal static Episode Episode(string name, string path = "/media/anime/Show/Season 2/Show - S02E03 - TBA WEBDL-1080p.mkv") =>
        new() { Id = Guid.NewGuid(), Name = name, Path = path, ParentIndexNumber = 2, IndexNumber = 3 };
}

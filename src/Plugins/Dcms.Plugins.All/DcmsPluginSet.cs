using Dcms.Plugins.Analytics;
using Dcms.Plugins.Articles;
using Dcms.Plugins.AudioLibrary;
using Dcms.Plugins.Blog;
using Dcms.Plugins.Branding;
using Dcms.Plugins.Carousel;
using Dcms.Plugins.Events;
using Dcms.Plugins.Facebook;
using Dcms.Plugins.FileDownloads;
using Dcms.Plugins.Forms;
using Dcms.Plugins.ImageGallery;
using Dcms.Plugins.Instagram;
using Dcms.Plugins.LiveChat;
using Dcms.Plugins.Roster;
using Dcms.Plugins.Search;
using Dcms.Plugins.VideoGallery;
using Dcms.Plugins.VideoStreaming;
using Dcms.Plugins.VisitorAuth;
using Dcms.PluginSdk.Runtime;

namespace Dcms.Plugins.All;

public static class DcmsPluginSet
{
    /// <summary>Registers the full built-in plugin set. Single source of truth for both content-api (runtime) and admin-api (catalog).</summary>
    public static PluginRegistryBuilder AddAll(this PluginRegistryBuilder builder) => builder
        .Add<ArticlesPlugin>()
        .Add<AnalyticsPlugin>()
        .Add<AudioLibraryPlugin>()
        .Add<BlogPlugin>()
        .Add<BrandingPlugin>()
        .Add<CarouselPlugin>()
        .Add<EventsPlugin>()
        .Add<FacebookPlugin>()
        .Add<FileDownloadsPlugin>()
        .Add<FormsPlugin>()
        .Add<ImageGalleryPlugin>()
        .Add<InstagramPlugin>()
        .Add<LiveChatPlugin>()
        .Add<RosterPlugin>()
        .Add<SearchPlugin>()
        .Add<VideoGalleryPlugin>()
        .Add<VideoStreamingPlugin>()
        .Add<VisitorAuthPlugin>();
}

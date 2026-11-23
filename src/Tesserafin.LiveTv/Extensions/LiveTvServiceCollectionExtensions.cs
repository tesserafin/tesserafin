using Microsoft.Extensions.DependencyInjection;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.LiveTv;
using Tesserafin.LiveTv.Channels;
using Tesserafin.LiveTv.Guide;
using Tesserafin.LiveTv.IO;
using Tesserafin.LiveTv.Listings;
using Tesserafin.LiveTv.Recordings;
using Tesserafin.LiveTv.Timers;
using Tesserafin.LiveTv.TunerHosts;
using Tesserafin.LiveTv.TunerHosts.HdHomerun;
using Tesserafin.Model.IO;

namespace Tesserafin.LiveTv.Extensions;

/// <summary>
/// Live TV extensions for <see cref="IServiceCollection"/>.
/// </summary>
public static class LiveTvServiceCollectionExtensions
{
    /// <summary>
    /// Adds Live TV services to the <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    public static void AddLiveTvServices(this IServiceCollection services)
    {
        services.AddSingleton<LiveTvDtoService>();
        services.AddSingleton<TimerManager>();
        services.AddSingleton<SeriesTimerManager>();
        services.AddSingleton<RecordingsMetadataManager>();
        services.AddSingleton<RecordingRetentionReader>();

        services.AddSingleton<ILiveTvManager, LiveTvManager>();
        services.AddSingleton<IChannelManager, ChannelManager>();
        services.AddSingleton<IStreamHelper, StreamHelper>();
        services.AddSingleton<ITunerHostManager, TunerHostManager>();
        services.AddSingleton<IListingsManager, ListingsManager>();
        services.AddSingleton<IGuideManager, GuideManager>();
        services.AddSingleton<IRecordingsManager, RecordingsManager>();

        services.AddSingleton<ILiveTvService, DefaultLiveTvService>();
        services.AddSingleton<ITunerHost, HdHomerunHost>();
        services.AddSingleton<ITunerHost, M3UTunerHost>();
        services.AddSingleton<SchedulesDirect>();
        services.AddSingleton<IListingsProvider>(s => s.GetRequiredService<SchedulesDirect>());
        services.AddSingleton<ISchedulesDirectService>(s => s.GetRequiredService<SchedulesDirect>());
        services.AddSingleton<IListingsProvider, XmlTvListingsProvider>();
    }
}

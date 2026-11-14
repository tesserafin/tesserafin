using System.Globalization;
using System.Threading.Tasks;
using Tesserafin.Controller.Events;
using Tesserafin.Controller.Events.Session;
using Tesserafin.Data.Entities;
using Tesserafin.Model.Activity;
using Tesserafin.Model.Globalization;

namespace Tesserafin.Server.Implementations.Events.Consumers.Session
{
    /// <summary>
    /// Creates an entry in the activity log when a session is started.
    /// </summary>
    public class SessionStartedLogger : IEventConsumer<SessionStartedEventArgs>
    {
        private readonly ILocalizationManager _localizationManager;
        private readonly IActivityManager _activityManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="SessionStartedLogger"/> class.
        /// </summary>
        /// <param name="localizationManager">The localization manager.</param>
        /// <param name="activityManager">The activity manager.</param>
        public SessionStartedLogger(ILocalizationManager localizationManager, IActivityManager activityManager)
        {
            _localizationManager = localizationManager;
            _activityManager = activityManager;
        }

        /// <inheritdoc />
        public async Task OnEvent(SessionStartedEventArgs eventArgs)
        {
            if (string.IsNullOrEmpty(eventArgs.Argument.UserName))
            {
                return;
            }

            await _activityManager.CreateAsync(new ActivityLog(
                string.Format(
                    CultureInfo.InvariantCulture,
                    _localizationManager.GetServerLocalizedString("UserOnlineFromDevice"),
                    eventArgs.Argument.UserName,
                    eventArgs.Argument.DeviceName),
                "SessionStarted",
                eventArgs.Argument.UserId)
            {
                ShortOverview = string.Format(
                    CultureInfo.InvariantCulture,
                    _localizationManager.GetServerLocalizedString("LabelIpAddressValue"),
                    eventArgs.Argument.RemoteEndPoint)
            }).ConfigureAwait(false);
        }
    }
}

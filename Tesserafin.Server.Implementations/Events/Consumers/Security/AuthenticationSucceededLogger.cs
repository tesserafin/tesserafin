using System.Globalization;
using System.Threading.Tasks;
using Tesserafin.Controller.Events;
using Tesserafin.Controller.Events.Authentication;
using Tesserafin.Data.Entities;
using Tesserafin.Model.Activity;
using Tesserafin.Model.Globalization;

namespace Tesserafin.Server.Implementations.Events.Consumers.Security
{
    /// <summary>
    /// Creates an entry in the activity log when there is a successful login attempt.
    /// </summary>
    public class AuthenticationSucceededLogger : IEventConsumer<AuthenticationResultEventArgs>
    {
        private readonly ILocalizationManager _localizationManager;
        private readonly IActivityManager _activityManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="AuthenticationSucceededLogger"/> class.
        /// </summary>
        /// <param name="localizationManager">The localization manager.</param>
        /// <param name="activityManager">The activity manager.</param>
        public AuthenticationSucceededLogger(ILocalizationManager localizationManager, IActivityManager activityManager)
        {
            _localizationManager = localizationManager;
            _activityManager = activityManager;
        }

        /// <inheritdoc />
        public async Task OnEvent(AuthenticationResultEventArgs eventArgs)
        {
            await _activityManager.CreateAsync(new ActivityLog(
                string.Format(
                    CultureInfo.InvariantCulture,
                    _localizationManager.GetServerLocalizedString("AuthenticationSucceededWithUserName"),
                    eventArgs.User.Name),
                "AuthenticationSucceeded",
                eventArgs.User.Id)
            {
                ShortOverview = string.Format(
                    CultureInfo.InvariantCulture,
                    _localizationManager.GetServerLocalizedString("LabelIpAddressValue"),
                    eventArgs.SessionInfo?.RemoteEndPoint),
            }).ConfigureAwait(false);
        }
    }
}

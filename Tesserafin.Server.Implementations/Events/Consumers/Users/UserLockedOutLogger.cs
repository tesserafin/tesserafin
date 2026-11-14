using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Events;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Events.Users;
using Tesserafin.Model.Activity;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.Notifications;

namespace Tesserafin.Server.Implementations.Events.Consumers.Users
{
    /// <summary>
    /// Creates an entry in the activity log when a user is locked out.
    /// </summary>
    public class UserLockedOutLogger : IEventConsumer<UserLockedOutEventArgs>
    {
        private readonly ILocalizationManager _localizationManager;
        private readonly IActivityManager _activityManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserLockedOutLogger"/> class.
        /// </summary>
        /// <param name="localizationManager">The localization manager.</param>
        /// <param name="activityManager">The activity manager.</param>
        public UserLockedOutLogger(ILocalizationManager localizationManager, IActivityManager activityManager)
        {
            _localizationManager = localizationManager;
            _activityManager = activityManager;
        }

        /// <inheritdoc />
        public async Task OnEvent(UserLockedOutEventArgs eventArgs)
        {
            await _activityManager.CreateAsync(new ActivityLog(
                string.Format(
                    CultureInfo.InvariantCulture,
                    _localizationManager.GetServerLocalizedString("UserLockedOutWithName"),
                    eventArgs.Argument.Username),
                NotificationType.UserLockedOut.ToString(),
                eventArgs.Argument.Id)
            {
                LogSeverity = LogLevel.Error
            }).ConfigureAwait(false);
        }
    }
}

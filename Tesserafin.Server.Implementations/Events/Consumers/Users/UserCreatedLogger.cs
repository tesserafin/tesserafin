using System.Globalization;
using System.Threading.Tasks;
using Tesserafin.Controller.Events;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Events.Users;
using Tesserafin.Model.Activity;
using Tesserafin.Model.Globalization;

namespace Tesserafin.Server.Implementations.Events.Consumers.Users
{
    /// <summary>
    /// Creates an entry in the activity log when a user is created.
    /// </summary>
    public class UserCreatedLogger : IEventConsumer<UserCreatedEventArgs>
    {
        private readonly ILocalizationManager _localizationManager;
        private readonly IActivityManager _activityManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserCreatedLogger"/> class.
        /// </summary>
        /// <param name="localizationManager">The localization manager.</param>
        /// <param name="activityManager">The activity manager.</param>
        public UserCreatedLogger(ILocalizationManager localizationManager, IActivityManager activityManager)
        {
            _localizationManager = localizationManager;
            _activityManager = activityManager;
        }

        /// <inheritdoc />
        public async Task OnEvent(UserCreatedEventArgs eventArgs)
        {
            await _activityManager.CreateAsync(new ActivityLog(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        _localizationManager.GetServerLocalizedString("UserCreatedWithName"),
                        eventArgs.Argument.Username),
                    "UserCreated",
                    eventArgs.Argument.Id))
                .ConfigureAwait(false);
        }
    }
}

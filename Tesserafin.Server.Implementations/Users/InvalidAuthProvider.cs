using System.Threading.Tasks;
using Tesserafin.Controller.Authentication;
using Tesserafin.Data.Entities;

namespace Tesserafin.Server.Implementations.Users
{
    /// <summary>
    /// An invalid authentication provider.
    /// </summary>
    public class InvalidAuthProvider : IAuthenticationProvider
    {
        /// <inheritdoc />
        public string Name => "InvalidOrMissingAuthenticationProvider";

        /// <inheritdoc />
        public bool IsEnabled => false;

        /// <inheritdoc />
        public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
        {
            throw new AuthenticationException("User Account cannot login with this provider. The Normal provider for this user cannot be found");
        }

        /// <inheritdoc />
        public Task ChangePassword(User user, string newPassword)
        {
            return Task.CompletedTask;
        }
    }
}

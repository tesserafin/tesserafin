using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Tesserafin.Api.Extensions;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.SyncPlay;
using Tesserafin.Data.Enums;

namespace Tesserafin.Api.Auth.SyncPlayAccessPolicy
{
    /// <summary>
    /// Default authorization handler.
    /// </summary>
    public class SyncPlayAccessHandler : AuthorizationHandler<SyncPlayAccessRequirement>
    {
        private readonly ISyncPlayManager _syncPlayManager;
        private readonly IUserManager _userManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="SyncPlayAccessHandler"/> class.
        /// </summary>
        /// <param name="syncPlayManager">Instance of the <see cref="ISyncPlayManager"/> interface.</param>
        /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
        public SyncPlayAccessHandler(
            ISyncPlayManager syncPlayManager,
            IUserManager userManager)
        {
            _syncPlayManager = syncPlayManager;
            _userManager = userManager;
        }

        /// <inheritdoc />
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SyncPlayAccessRequirement requirement)
        {
            var userId = context.User.GetUserId();
            var user = _userManager.GetUserById(userId);
            if (user is null)
            {
                throw new ResourceNotFoundException();
            }

            if (requirement.RequiredAccess == SyncPlayAccessRequirementType.HasAccess)
            {
                if (user.SyncPlayAccess is SyncPlayUserAccessType.CreateAndJoinGroups or SyncPlayUserAccessType.JoinGroups
                    || _syncPlayManager.IsUserActive(userId))
                {
                    context.Succeed(requirement);
                }
            }
            else if (requirement.RequiredAccess == SyncPlayAccessRequirementType.CreateGroup)
            {
                if (user.SyncPlayAccess == SyncPlayUserAccessType.CreateAndJoinGroups)
                {
                    context.Succeed(requirement);
                }
            }
            else if (requirement.RequiredAccess == SyncPlayAccessRequirementType.JoinGroup)
            {
                if (user.SyncPlayAccess == SyncPlayUserAccessType.CreateAndJoinGroups
                    || user.SyncPlayAccess == SyncPlayUserAccessType.JoinGroups)
                {
                    context.Succeed(requirement);
                }
            }
            else if (requirement.RequiredAccess == SyncPlayAccessRequirementType.IsInGroup)
            {
                if (_syncPlayManager.IsUserActive(userId))
                {
                    context.Succeed(requirement);
                }
            }

            return Task.CompletedTask;
        }
    }
}

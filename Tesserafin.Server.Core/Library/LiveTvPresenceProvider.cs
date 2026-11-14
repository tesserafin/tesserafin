using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Tesserafin.Common.Configuration;
using Tesserafin.Controller.Configuration;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.LiveTv;

namespace Tesserafin.Server.Core.Library
{
    /// <summary>
    /// Sole production implementation of <see cref="ILiveTvPresenceProvider"/> - Live TV
    /// enablement/folder presence, ported off <c>LiveTvManager.GetEnabledUsers</c>/
    /// <c>GetInternalLiveTvFolder</c> with behavior preserved exactly (RFC
    /// <c>docs/rfc-di-query-user-views-v2.md</c> §5, §8, §9, PR108).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Depends on <see cref="IUserManager"/> (enumerating users), <see cref="IServerConfigurationManager"/>
    /// (tuner host count) and <see cref="Lazy{IReadOnlyList}"/> of <see cref="ILiveTvService"/> (see
    /// below), <see cref="ILocalizationManager"/> (the folder's localized label) and
    /// <see cref="IUserViewFactory"/> (PR106b, for the folder itself). None of these reference
    /// <c>ILibraryManager</c>, <c>IUserViewManager</c>, <c>IChannelManager</c>, <c>ILiveTvManager</c>
    /// or <c>IDtoService</c> directly - this satisfies RFC invariant I1 (eager construction graph)
    /// for this leaf.
    /// </para>
    /// <para>
    /// <b>RFC deviation, reported (PR108 finding, escalation required)</b>: the RFC's §5/§8
    /// dependency list for this port was <c>IUserManager</c>, <c>ILocalizationManager</c>,
    /// <c>IUserViewFactory</c> only - it did not trace into <c>IsLiveTvEnabled</c>'s body. Porting it
    /// faithfully (<c>user.HasPermission(EnableLiveTvAccess) &amp;&amp; (Services.Count &gt; 1 ||
    /// config.GetLiveTvConfiguration().TunerHosts.Length &gt; 0)</c>) needs the count of registered
    /// <see cref="ILiveTvService"/> plugins. <see cref="IServerConfigurationManager"/> (tuner hosts)
    /// is an established leaf-safe type, but <b>a direct <see cref="IEnumerable{ILiveTvService}"/>
    /// injection is NOT leaf-safe here</b>, unlike the analogous <see cref="IEnumerable{IChannel}"/>
    /// dependency on <see cref="ChannelCatalog"/>: <c>ILiveTvService</c> has an in-tree registered
    /// implementation, <c>DefaultLiveTvService</c> (<c>LiveTvServiceCollectionExtensions.cs:40</c>),
    /// whose own constructor takes <c>ILibraryManager</c> directly (plus <c>LiveTvDtoService</c>,
    /// which also takes <c>ILibraryManager</c>) - a real SCC member. A direct
    /// <c>IEnumerable&lt;ILiveTvService&gt;</c> injection would therefore create the eager edge
    /// <c>LiveTvPresenceProvider -&gt; IEnumerable&lt;ILiveTvService&gt; -&gt; DefaultLiveTvService -&gt;
    /// ILibraryManager</c>, violating I1 exactly the way a direct (non-Lazy) <c>IProviderManager</c>
    /// injection would in <c>UserViewFactory</c> (RFC §2/§8). This was latent as long as
    /// <c>LibraryManager</c> still holds <c>Lazy&lt;IUserViewManager&gt;</c> (no eager cycle exists
    /// yet to detonate it), but would break DI construction once PR109/110 wire
    /// <c>IUserViewCatalog</c>/this port eagerly into <c>LibraryManager</c>'s graph.
    /// </para>
    /// <para>
    /// <b>Mitigation applied (same technique as <c>UserViewFactory</c>'s <c>Lazy&lt;IProviderManager&gt;</c>,
    /// RFC §2/§8)</b>: the service list is injected as
    /// <see cref="Lazy{IReadOnlyList}"/>&lt;<see cref="ILiveTvService"/>&gt;, excluded from the eager
    /// construction graph (I1) while still providing the real runtime dependency
    /// <see cref="GetEnabledUsers"/> needs. <b>This is not risk-free</b>: unlike the two I2
    /// exceptions already characterized and accepted in PR106b (<c>QueueRefresh</c> post-persistence;
    /// the new-item <c>UpdateToRepositoryAsync</c> static path), <c>.Value</c> here is evaluated on
    /// every <see cref="GetEnabledUsers"/> call, including from <c>UserViewManager.GetUserViews</c>
    /// (RFC §5's actual call site) - i.e. a real I2-relevant runtime edge into the SCC
    /// (<c>DefaultLiveTvService -&gt; ILibraryManager</c>) on the view-creation-adjacent path, not yet
    /// blessed by the RFC as a third assumed exception. <b>This needs explicit sign-off at the
    /// RFC/PR109-110 boundary</b> (add it to §8's exception list if accepted, or redesign - e.g. a
    /// narrower "service count" port - if not); it is not this PR's call to make unilaterally.
    /// </para>
    /// </remarks>
    internal sealed class LiveTvPresenceProvider : ILiveTvPresenceProvider
    {
        private readonly IUserManager _userManager;
        private readonly IServerConfigurationManager _config;
        private readonly ILocalizationManager _localization;
        private readonly IUserViewFactory _userViewFactory;
        private readonly Lazy<IReadOnlyList<ILiveTvService>> _servicesFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="LiveTvPresenceProvider"/> class.
        /// </summary>
        /// <param name="userManager">The user manager.</param>
        /// <param name="config">The server configuration manager (tuner host count; see type-level remarks).</param>
        /// <param name="localization">The localization manager (folder label).</param>
        /// <param name="userViewFactory">The user view factory leaf (PR106b, for the folder itself).</param>
        /// <param name="servicesFactory">
        /// The registered Live TV service plugins, lazily resolved - <b>must</b> stay
        /// <see cref="Lazy{T}"/>, never a direct <see cref="IEnumerable{ILiveTvService}"/> injection
        /// (see type-level remarks: RFC I1 finding, PR108).
        /// </param>
        public LiveTvPresenceProvider(
            IUserManager userManager,
            IServerConfigurationManager config,
            ILocalizationManager localization,
            IUserViewFactory userViewFactory,
            Lazy<IReadOnlyList<ILiveTvService>> servicesFactory)
        {
            _userManager = userManager;
            _config = config;
            _localization = localization;
            _userViewFactory = userViewFactory;
            _servicesFactory = servicesFactory;
        }

        private IReadOnlyList<ILiveTvService> Services => _servicesFactory.Value;

        /// <inheritdoc />
        public IEnumerable<User> GetEnabledUsers()
        {
            return _userManager.GetUsers().Where(IsLiveTvEnabled);
        }

        /// <inheritdoc />
        public Folder GetLiveTvFolder(CancellationToken cancellationToken)
        {
            var name = _localization.GetLocalizedString("HeaderLiveTV");
            return _userViewFactory.GetNamedView(name, CollectionType.livetv, name);
        }

        /// <summary>
        /// Identical to <c>LiveTvManager.IsLiveTvEnabled</c>: the user needs the
        /// <see cref="PermissionKind.EnableLiveTvAccess"/> permission, and either more than one Live
        /// TV service must be registered or at least one tuner host must be configured.
        /// </summary>
        private bool IsLiveTvEnabled(User user)
        {
            return user.HasPermission(PermissionKind.EnableLiveTvAccess)
                && (Services.Count > 1 || _config.GetConfiguration<LiveTvOptions>("livetv").TunerHosts.Length > 0);
        }
    }
}

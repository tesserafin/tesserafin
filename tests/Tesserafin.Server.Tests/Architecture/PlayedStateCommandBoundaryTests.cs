using System;
using System.Linq;
using System.Reflection;
using Tesserafin.Api.Controllers;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Tests.Architecture;

/// <summary>
/// #129 step 5: "mark played / unplayed" is a service command, not an entity method. The removed
/// entity commands may not come back on any media type, and the HTTP route depends on the service.
/// </summary>
public sealed class PlayedStateCommandBoundaryTests
{
    private static readonly string[] _removedCommands = ["MarkPlayed", "MarkUnplayed", "PropagatePlayedState", "ResetPlayedState"];

    [Fact]
    public void No_media_entity_carries_a_played_state_command()
    {
        var entityTypes = new[] { typeof(BaseItem).Assembly, typeof(ItemPlayedStateService).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(BaseItem).IsAssignableFrom(t));

        var offenders = entityTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => _removedCommands.Contains(m.Name, StringComparer.Ordinal))
                .Select(m => t.FullName + "." + m.Name))
            .ToList();

        Assert.True(offenders.Count == 0, "Played state commands belong to IItemPlayedStateService: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_playstate_route_takes_the_played_state_service()
    {
        var parameters = typeof(PlaystateController).GetConstructors().Single().GetParameters();

        Assert.Contains(parameters, p => p.ParameterType == typeof(IItemPlayedStateService));
    }
}

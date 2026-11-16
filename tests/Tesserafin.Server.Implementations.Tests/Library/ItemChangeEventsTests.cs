using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Server.Core.EntryPoints;
using Tesserafin.Server.Core.IO;
using Tesserafin.Server.Core.Library;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Library;

public sealed class ItemChangeEventsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Publication_IsolatesEachSubscriber_AndUnsubscriptionTakesEffect(int kind)
    {
        var changes = new ItemChangeEvents(NullLogger<ItemChangeEvents>.Instance);
        var item = new UserView { Id = Guid.NewGuid() };
        var parent = new Folder { Id = Guid.NewGuid() };
        var calls = 0;
        Subscribe((_, _) => throw new InvalidOperationException("broken first observer"));
        EventHandler<ItemChangeEventArgs> listener = (sender, args) =>
        {
            Assert.Same(changes, sender);
            Assert.Same(item, args.Item);
            Assert.Same(parent, args.Parent);
            Assert.Equal(kind == 1 ? ItemUpdateType.MetadataEdit : default, args.UpdateReason);
            calls++;
        };
        Subscribe(listener);
        Publish();
        Assert.Equal(1, calls);
        switch (kind)
        {
            case 0:
                changes.ItemAdded -= listener;
                break;
            case 1:
                changes.ItemUpdated -= listener;
                break;
            default:
                changes.ItemRemoved -= listener;
                break;
        }

        Publish();
        Assert.Equal(1, calls);

        void Subscribe(EventHandler<ItemChangeEventArgs> handler)
        {
            switch (kind)
            {
                case 0:
                    changes.ItemAdded += handler;
                    break;
                case 1:
                    changes.ItemUpdated += handler;
                    break;
                default:
                    changes.ItemRemoved += handler;
                    break;
            }
        }

        void Publish()
        {
            switch (kind)
            {
                case 0:
                    changes.Added(item, parent);
                    break;
                case 1:
                    changes.Updated(item, parent, ItemUpdateType.MetadataEdit);
                    break;
                default:
                    changes.Removed(item, parent);
                    break;
            }
        }
    }

    [Fact]
    public void Publication_UsesAnAdmissionSnapshot_WhenAnObserverUnsubscribesAnother()
    {
        var changes = new ItemChangeEvents(NullLogger<ItemChangeEvents>.Instance);
        var observed = 0;
        EventHandler<ItemChangeEventArgs> last = (_, _) => observed++;
        changes.ItemAdded += (_, _) => changes.ItemAdded -= last;
        changes.ItemAdded += last;
        var item = new UserView { Id = Guid.NewGuid() };
        changes.Added(item, null);
        Assert.Equal(1, observed);
        changes.Added(item, null);
        Assert.Equal(1, observed);
    }

    [Fact]
    public void NotificationOwnership_HasNoManagerRelaysOrWriterDependency()
    {
        foreach (var type in new[] { typeof(ILibraryManager), typeof(Tesserafin.Server.Core.Library.LibraryManager), typeof(IItemStore), typeof(ItemStore), typeof(IItemImageService), typeof(ItemImageService) })
        {
            Assert.Empty(type.GetEvents());
            Assert.Null(type.GetMethod("ReportItemRemoved"));
        }

        Assert.DoesNotContain(typeof(IItemChangeEvents).GetMethods(), m => !m.IsSpecialName);
        Assert.Empty(typeof(IItemChangePublisher).GetEvents());
        var parameters = typeof(ItemChangeEvents).GetConstructors().Single().GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(Microsoft.Extensions.Logging.ILogger<ItemChangeEvents>), parameters[0].ParameterType);
        Assert.DoesNotContain(typeof(IItemStore), typeof(Tesserafin.Server.Core.Library.LibraryManager).GetConstructors().Single().GetParameters().Select(p => p.ParameterType));
        foreach (var type in new[] { typeof(LibraryMonitor), typeof(LibraryChangedNotifier) })
        {
            Assert.Contains(typeof(IItemChangeEvents), type.GetConstructors().Single().GetParameters().Select(p => p.ParameterType));
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Tesserafin.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory.FullName, "Tesserafin.Server.Core/Library/LibraryManager.cs"));
        foreach (var relay in new[] { "ItemAdded +=", "ItemUpdated +=", "ItemRemoved +=", "ItemSaved +=", "ImagesChanged +=" })
        {
            Assert.DoesNotContain(relay, source, StringComparison.Ordinal);
        }
    }
}

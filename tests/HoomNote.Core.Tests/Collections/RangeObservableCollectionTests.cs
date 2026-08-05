using System.Collections.Specialized;
using HoomNote.Core.Collections;

namespace HoomNote.Core.Tests.Collections;

public sealed class RangeObservableCollectionTests
{
    [Fact]
    public void ReplaceAll_ReplacesContentsWithOneResetNotification()
    {
        var collection = new RangeObservableCollection<int> { 1, 2 };
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, args) => notifications.Add(args);

        collection.ReplaceAll([3, 4, 5]);

        Assert.Equal([3, 4, 5], collection);
        var notification = Assert.Single(notifications);
        Assert.Equal(NotifyCollectionChangedAction.Reset, notification.Action);
    }
}

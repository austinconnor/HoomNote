using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace HoomNote.Core.Collections;

/// <summary>
/// An observable collection that can replace its contents with a single reset notification.
/// This avoids making virtualized controls process one remove/add notification per item.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();

        Items.Clear();
        foreach (var item in items) Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Reset));
    }
}

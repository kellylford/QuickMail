using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace QuickMail.Helpers;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can batch multiple mutations into a single
/// <see cref="NotifyCollectionChangedAction.Reset"/> notification.
/// <para>
/// Call <see cref="BeginBatch"/> before a burst of inserts/removes, then <see cref="EndBatch"/>
/// afterwards.  During the batch no <see cref="INotifyCollectionChanged.CollectionChanged"/> or
/// <see cref="INotifyPropertyChanged.PropertyChanged"/> events are raised.  Calling
/// <see cref="EndBatch"/> fires a single <c>Reset</c> event if any mutations occurred, letting
/// the bound <c>ListView</c> emit one UIA <c>StructureChanged</c> notification instead of one
/// per insert — which prevents screen readers from re-announcing the focused item after every
/// individual insert during background sync.
/// </para>
/// </summary>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    private bool _batchActive;
    private bool _pendingReset;

    public BatchObservableCollection() { }
    public BatchObservableCollection(IEnumerable<T> collection) : base(collection) { }

    /// <summary>Begin suppressing individual <see cref="CollectionChanged"/> events.</summary>
    public void BeginBatch()
    {
        _batchActive = true;
        _pendingReset = false;
    }

    /// <summary>
    /// End the batch.  If any mutations occurred during the batch a single
    /// <see cref="NotifyCollectionChangedAction.Reset"/> event is raised.
    /// </summary>
    public void EndBatch()
    {
        _batchActive = false;
        if (!_pendingReset) return;
        _pendingReset = false;
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_batchActive)
        {
            _pendingReset = true;
            return;
        }
        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_batchActive) return;
        base.OnPropertyChanged(e);
    }
}

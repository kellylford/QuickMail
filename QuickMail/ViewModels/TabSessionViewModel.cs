using System;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickMail.Models;

namespace QuickMail.ViewModels;

/// <summary>
/// Base class for all tab view models. Subclasses represent a single open tab
/// (message, address book, etc.) in the tab strip.
/// </summary>
public abstract partial class TabSessionViewModel : ObservableObject
{
    public TabSessionModel Model { get; }

    protected TabSessionViewModel(TabSessionModel model)
    {
        Model = model;
        _title = model.Title;
        _isDirty = model.IsDirty;
        _canClose = model.CanClose;
    }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _canClose = true;

    partial void OnTitleChanged(string value) => Model.Title = value;
    partial void OnIsDirtyChanged(bool value) => Model.IsDirty = value;

    /// <summary>True if the tab can be closed right now (no unsaved work, or user confirmed).</summary>
    public virtual bool CanCloseNow() => !IsDirty || !CanClose;

    public event Action<TabSessionViewModel>? CloseRequested;
    protected void RequestClose() => CloseRequested?.Invoke(this);
}

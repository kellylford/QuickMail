using System;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Persists the user's spoken field layouts for message list rows.
/// </summary>
public interface IRowLayoutService
{
    /// <summary>
    /// Loads the layouts, reconciled against the current catalog. Never returns null and never
    /// throws — a missing or corrupt file yields defaults.
    /// </summary>
    RowLayouts Load();

    /// <summary>Writes the layouts and raises <see cref="LayoutsChanged"/>.</summary>
    void Save(RowLayouts layouts);

    /// <summary>Raised after a successful <see cref="Save"/>.</summary>
    event EventHandler? LayoutsChanged;
}

using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QuickMail.Models;

public partial class FlagDefinition : ObservableObject
{
    /// <summary>Well-known ID for the built-in "Flagged" flag. Never changes across installs.</summary>
    public static readonly Guid BuiltInFlagId = new("00000000-0000-0000-0000-000000000001");

    [ObservableProperty]
    private Guid _id = Guid.NewGuid();

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _colorHex = "#C05621";

    [ObservableProperty]
    private int _sortOrder;

    /// <summary>True for the built-in "Flagged" flag, which cannot be deleted.</summary>
    [ObservableProperty]
    private bool _isBuiltIn;

    public static FlagDefinition CreateBuiltIn() => new()
    {
        Id        = BuiltInFlagId,
        Name      = "Flagged",
        ColorHex  = "#C05621",
        SortOrder = 0,
        IsBuiltIn = true,
    };
}

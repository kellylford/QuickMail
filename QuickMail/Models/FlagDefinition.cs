using System;

namespace QuickMail.Models;

public class FlagDefinition
{
    /// <summary>Well-known ID for the built-in "Flagged" flag. Never changes across installs.</summary>
    public static readonly Guid BuiltInFlagId = new("00000000-0000-0000-0000-000000000001");

    public Guid   Id        { get; set; } = Guid.NewGuid();
    public string Name      { get; set; } = string.Empty;
    public string ColorHex  { get; set; } = "#FF8C00";
    public int    SortOrder { get; set; }

    /// <summary>True for the built-in "Flagged" flag, which cannot be deleted.</summary>
    public bool   IsBuiltIn { get; set; }

    public static FlagDefinition CreateBuiltIn() => new()
    {
        Id        = BuiltInFlagId,
        Name      = "Flagged",
        ColorHex  = "#FF8C00",
        SortOrder = 0,
        IsBuiltIn = true,
    };
}

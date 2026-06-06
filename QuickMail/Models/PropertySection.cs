using System.Collections.Generic;

namespace QuickMail.Models;

/// <summary>A named group of PropertyItem rows within a Properties dialog.</summary>
public sealed record PropertySection(string Header, IReadOnlyList<PropertyItem> Items);

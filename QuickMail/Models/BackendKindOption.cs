namespace QuickMail.Models;

/// <summary>
/// A selectable backend option for the Add Account dialog's backend combo box.
/// <see cref="Label"/> is the user-facing text; <see cref="ToString"/> returns it
/// so the combo renders correctly without an explicit DisplayMemberPath.
/// </summary>
public record BackendKindOption(BackendKind Kind, string Label)
{
    public override string ToString() => Label;
}

namespace QuickMail.Models;

public class MessageTemplate
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    // A screen reader reads a data-bound Selector item's UIA Name from ToString()
    // (DisplayMemberPath only sets the visual). Without this the template picker
    // announces "QuickMail.Models.MessageTemplate" for every row. See CLAUDE.md.
    public override string ToString() => Title;
}

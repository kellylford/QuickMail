namespace QuickMail.Models;

public class MessageTemplate
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}

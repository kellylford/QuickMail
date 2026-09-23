namespace QuickMail.Helpers;

/// <summary>
/// Wording for the kind of block at the caret, shared by the HTML and Markdown
/// editors so both say the same thing — in the navigation announcement, Ctrl+T
/// and Show Formatting.
/// </summary>
public static class BlockLabels
{
    /// <summary>
    /// Adds the quote to a block label. A plain paragraph in a quote is simply
    /// "Quote"; anything else keeps its own name first ("Heading 2, quote").
    /// Nesting follows the list pattern: "Quote, level 2".
    /// </summary>
    public static string WithQuote(string blockLabel, int quoteDepth)
    {
        if (quoteDepth <= 0) return blockLabel;
        var quote = quoteDepth == 1 ? "Quote" : $"Quote, level {quoteDepth}";
        if (blockLabel == "Normal text") return quote;
        return quoteDepth == 1 ? $"{blockLabel}, quote" : $"{blockLabel}, quote level {quoteDepth}";
    }
}

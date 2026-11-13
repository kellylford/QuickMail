// Tests for AddressInput, which reads text typed or pasted into To, Cc and Bcc (#979, #980).

using System.Linq;
using QuickMail.Helpers;
using Xunit;

namespace QuickMail.Tests;

public class AddressInputTests
{
    private static string[] Addresses(string text) =>
        AddressInput.Parse(text).Mailboxes.Select(m => m.Address).ToArray();

    [Theory]
    [InlineData("ann@example.com; ben@example.com")]
    [InlineData("ann@example.com;ben@example.com")]
    [InlineData("ann@example.com, ben@example.com")]
    [InlineData("ann@example.com\r\nben@example.com")]
    [InlineData("ann@example.com\nben@example.com")]
    [InlineData("ann@example.com\tben@example.com")]
    [InlineData("ann@example.com; ben@example.com;")]
    [InlineData("  ann@example.com ;  ben@example.com  ")]
    public void Separated_lists_give_one_address_each(string text) =>
        Assert.Equal(new[] { "ann@example.com", "ben@example.com" }, Addresses(text));

    [Fact]
    public void The_format_QuickMail_copies_chips_in_round_trips()
    {
        // TokenizedAddressBox copies several chips joined with "; " (#979).
        var result = AddressInput.Parse("ann@example.com; Ben Baker <ben@example.com>; cat@example.com");

        Assert.Equal(3, result.Mailboxes.Count);
        Assert.Equal(new AddressInput.Mailbox("Ben Baker", "ben@example.com"), result.Mailboxes[1]);
        Assert.Equal(string.Empty, result.Leftover);
    }

    [Fact]
    public void Outlook_style_list_with_comma_names_keeps_each_person_whole()
    {
        var result = AddressInput.Parse("Smith, John <john@example.com>; Doe, Jane <jane@example.com>;");

        Assert.Equal(new[] { "john@example.com", "jane@example.com" }, result.Mailboxes.Select(m => m.Address));
        Assert.Equal("Smith, John", result.Mailboxes[0].Name);
        Assert.Equal("Doe, Jane", result.Mailboxes[1].Name);
    }

    [Fact]
    public void Single_unquoted_last_first_name_stays_one_address() =>
        Assert.Equal(new[] { "john@example.com" }, Addresses("Smith, John <john@example.com>"));

    [Fact]
    public void Semicolon_inside_a_quoted_name_does_not_split()
    {
        var result = AddressInput.Parse("\"Doe; Jane\" <jane@example.com>; ann@example.com");

        Assert.Equal(2, result.Mailboxes.Count);
        Assert.Equal("Doe; Jane", result.Mailboxes[0].Name);
    }

    [Fact]
    public void Quoted_comma_name_and_a_bare_address() =>
        Assert.Equal(new[] { "quoted@example.com", "plain@example.com" },
            Addresses("\"Quoted, Name\" <quoted@example.com>, plain@example.com"));

    [Fact]
    public void Group_syntax_yields_its_members() =>
        Assert.Equal(new[] { "a@example.com", "b@example.com" }, Addresses("Team: a@example.com, b@example.com;"));

    [Fact]
    public void Text_with_no_address_is_returned_as_leftover()
    {
        var result = AddressInput.Parse("hello world");

        Assert.Empty(result.Mailboxes);
        Assert.Equal("hello world", result.Leftover);
    }

    [Fact]
    public void Bare_last_first_name_is_leftover_not_split()
    {
        // TokenizedAddressBoxPasteTests relies on this: the name goes back to the box whole.
        var result = AddressInput.Parse("Ford, Kelly");

        Assert.Empty(result.Mailboxes);
        Assert.Equal("Ford, Kelly", result.Leftover);
    }

    [Fact]
    public void Addresses_and_junk_mixed_keep_both()
    {
        var result = AddressInput.Parse("ann@example.com; not an address; ben@example.com");

        Assert.Equal(new[] { "ann@example.com", "ben@example.com" }, result.Mailboxes.Select(m => m.Address));
        Assert.Equal("not an address", result.Leftover);
    }

    [Fact]
    public void Unparseable_text_with_an_at_sign_is_kept_whole_for_validation() =>
        Assert.Equal(new[] { "bad@@example" }, Addresses("bad@@example"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ;\r\n ; ")]
    public void Empty_input_gives_nothing(string? text)
    {
        var result = AddressInput.Parse(text);

        Assert.Empty(result.Mailboxes);
        Assert.Equal(string.Empty, result.Leftover);
    }

    [Fact]
    public void Split_keeps_angle_brackets_together() =>
        Assert.Equal(new[] { "A <a;b@example.com>", "c@example.com" },
            AddressInput.SplitSegments("A <a;b@example.com>; c@example.com"));

    [Fact]
    public void Semicolon_inside_a_comment_does_not_split()
    {
        // Review of #979: raw header text can carry RFC 5322 comments.
        var result = AddressInput.Parse("ann@example.com (Ann; Sales), ben@example.com");

        Assert.Equal(new[] { "ann@example.com", "ben@example.com" }, result.Mailboxes.Select(m => m.Address));
        Assert.Equal(string.Empty, result.Leftover);
    }

    [Fact]
    public void Empty_group_is_dropped_not_left_over()
    {
        // A Bcc-only draft from another client has "To: undisclosed-recipients:;".
        var result = AddressInput.Parse("undisclosed-recipients:;");

        Assert.Empty(result.Mailboxes);
        Assert.Equal(string.Empty, result.Leftover);
    }

    [Theory]
    [InlineData("", "ann@example.com", "ann@example.com")]
    [InlineData("Ann Lee", "ann@example.com", "Ann Lee <ann@example.com>")]
    [InlineData("Smith, John", "john@example.com", "\"Smith, John\" <john@example.com>")]
    [InlineData("Doe; Jane", "jane@example.com", "\"Doe; Jane\" <jane@example.com>")]
    public void Format_quotes_names_with_specials(string name, string address, string expected) =>
        Assert.Equal(expected, AddressInput.Format(name, address));

    [Fact]
    public void Formatted_addresses_read_back_as_the_same_people()
    {
        var people = new[]
        {
            new AddressInput.Mailbox("Smith, John", "john@example.com"),
            new AddressInput.Mailbox("Doe; Jane", "jane@example.com"),
            new AddressInput.Mailbox("", "ann@example.com"),
        };
        var text = string.Join("; ", people.Select(p => AddressInput.Format(p.Name, p.Address)));

        Assert.Equal(people, AddressInput.Parse(text).Mailboxes);
    }

    [Fact]
    public void AddTo_keeps_comma_names_as_one_recipient()
    {
        // The send path used to split on every comma, sending to an address "Smith".
        var list = new MimeKit.InternetAddressList();
        AddressInput.AddTo(list, "Smith, John <john@example.com>; \"Doe, Jane\" <jane@example.com>");

        var mailboxes = list.Mailboxes.ToList();
        Assert.Equal(new[] { "john@example.com", "jane@example.com" }, mailboxes.Select(m => m.Address));
        Assert.Equal(new[] { "Smith, John", "Doe, Jane" }, mailboxes.Select(m => m.Name));
    }

    [Fact]
    public void AddTo_reads_comma_and_semicolon_lists()
    {
        var list = new MimeKit.InternetAddressList();
        AddressInput.AddTo(list, "ann@example.com, ben@example.com; cat@example.com");

        Assert.Equal(new[] { "ann@example.com", "ben@example.com", "cat@example.com" }, list.Mailboxes.Select(m => m.Address));
    }

    [Fact]
    public void AddTo_one_bad_entry_does_not_lose_its_neighbours()
    {
        var list = new MimeKit.InternetAddressList();
        AddressInput.AddTo(list, "ann@example.com, <<<, ben@example.com");

        Assert.Equal(new[] { "ann@example.com", "ben@example.com" }, list.Mailboxes.Select(m => m.Address));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddTo_empty_adds_nothing(string? text)
    {
        var list = new MimeKit.InternetAddressList();
        AddressInput.AddTo(list, text);
        Assert.Empty(list);
    }

    [Theory]
    [InlineData("ann@example.com\r\nben@example.com\r\ncat@example.com", "ann@example.com; ben@example.com; cat@example.com")]
    [InlineData("ann@example.com\nben@example.com", "ann@example.com; ben@example.com")]
    [InlineData("ann@example.com\r\n", "ann@example.com")]
    [InlineData("ann@example.com\r\n\r\n\r\nben@example.com\r\n", "ann@example.com; ben@example.com")]
    [InlineData("Ann\tann@example.com", "Ann; ann@example.com")]
    public void Flatten_turns_line_breaks_and_tabs_into_separators(string text, string expected) =>
        Assert.Equal(expected, AddressInput.FlattenForSingleLine(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ann@example.com; ben@example.com")]
    public void Flatten_leaves_single_line_text_alone(string? text) =>
        Assert.Null(AddressInput.FlattenForSingleLine(text));
}

using System.Collections.Generic;
using QuickMail.Models;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The rule editor must refuse a Move or Delete rule that tests nothing.
///
/// <para>A rule with no condition matches every message (see
/// <c>RuleServiceTests.TestRule_EmptyConditions_MatchesEverything</c>), and rules run on Inbox mail
/// as it arrives and through Run on Existing Mail — so a condition-less Delete empties the Inbox. The
/// client-only rules window refused this; when every account moved onto this editor (#412 for
/// Microsoft 365, #550 for the rest) the check did not come with it.</para>
///
/// <para>"No condition" means what the SAVED rule tests, not what the form shows. A condition
/// switched on but left empty, and one holding text but switched off (#665), are both absent from the
/// saved rule, so neither may count. Those two are the cases a check written against the raw text
/// properties would get wrong in opposite directions.</para>
/// </summary>
public class RuleEditorValidationTests
{
    private static ServerRuleEditorViewModel Named(string name = "Clean up")
    {
        var vm = ServerRuleEditorViewModel.ForNew();
        vm.Name = name;
        return vm;
    }

    [Fact]
    public void Delete_WithNoCondition_IsRefused_AndSaysWhy()
    {
        var vm = Named();
        vm.Delete = true;
        var announced = new List<string>();
        vm.AnnouncementRequested += (text, _) => announced.Add(text);

        Assert.False(vm.Validate());
        Assert.Equal(ServerRuleEditorViewModel.NoConditionError, vm.ActionsError);
        Assert.Equal([ServerRuleEditorViewModel.NoConditionError], announced);
    }

    [Fact]
    public void Move_WithNoCondition_IsRefused()
    {
        var vm = Named();
        vm.MoveToFolder = true;
        vm.MoveToFolderId = "folder-1";
        vm.MoveToFolderName = "Archive";

        Assert.False(vm.Validate());
        Assert.Equal(ServerRuleEditorViewModel.NoConditionError, vm.ActionsError);
        Assert.Equal(string.Empty, vm.FolderError);   // the folder is fine; the conditions are not
    }

    [Fact]
    public void AConditionSwitchedOnButEmpty_DoesNotCount()
    {
        // A new rule opens with every condition switched ON and empty. That is exactly the form a
        // user reaches by typing a name, ticking Delete and pressing Save — the case this guards.
        var vm = Named();
        Assert.True(vm.UseSubjectContains);
        Assert.True(vm.UseFromAddresses);
        vm.Delete = true;

        Assert.False(vm.Validate());
    }

    [Fact]
    public void AConditionWithTextButSwitchedOff_DoesNotCount()
    {
        var vm = Named();
        vm.Delete = true;
        vm.SubjectContains = "newsletter";
        vm.UseSubjectContains = false;

        Assert.False(vm.Validate());
        Assert.Equal("newsletter", vm.SubjectContains);   // switching off keeps the text (#665)
    }

    [Fact]
    public void Delete_WithOneRealCondition_IsAccepted()
    {
        var vm = Named();
        vm.Delete = true;
        vm.SubjectContains = "newsletter";

        Assert.True(vm.Validate());
        Assert.Equal(string.Empty, vm.ActionsError);
    }

    [Theory]
    [InlineData(",")]
    [InlineData(";")]
    [InlineData(" , ; ")]
    public void AnAddressFieldHoldingOnlySeparators_DoesNotCount(string separators)
    {
        // Saving splits the address fields and drops empty entries, so "," reaches the saved rule as
        // no address at all — a condition-less Delete. A check that merely asked whether the field was
        // blank let this through; the field has to be parsed the way saving parses it. Both address
        // fields, because each is its own way in.
        var from = Named();
        from.Delete = true;
        from.FromAddresses = separators;
        Assert.False(from.Validate());

        var sentTo = Named();
        sentTo.Delete = true;
        sentTo.SentToAddresses = separators;
        Assert.False(sentTo.Validate());
    }

    [Fact]
    public void ARealAddress_Counts()
    {
        // The other side of the separator case: parsing must still recognise an actual address.
        var vm = Named();
        vm.Delete = true;
        vm.FromAddresses = "boss@work.com";

        Assert.True(vm.Validate());
    }

    [Fact]
    public void AnOnOffCondition_Counts()
    {
        // Not every condition is free text; "has attachments" is a condition in its own right.
        var vm = Named();
        vm.Delete = true;
        vm.HasAttachments = true;

        Assert.True(vm.Validate());
    }

    [Fact]
    public void MarkAsRead_WithNoCondition_IsStillAllowed()
    {
        // Parity with the client-only window: marking everything read is recoverable and sometimes
        // wanted. Only the actions that take mail out of the Inbox need a condition.
        var vm = Named();
        vm.MarkAsRead = true;

        Assert.True(vm.Validate());
    }

    [Fact]
    public void MoveWithNoFolderAndNoCondition_ReportsBoth_WithoutLosingEither()
    {
        // The guard appends to an existing action message rather than replacing it, so a form with
        // two things wrong says both.
        var vm = Named();
        vm.MoveToFolder = true;   // no folder chosen, so HasAnyAction is false as well

        Assert.False(vm.Validate());
        Assert.StartsWith("Choose at least one action.", vm.ActionsError);
        Assert.EndsWith(ServerRuleEditorViewModel.NoConditionError, vm.ActionsError);
        Assert.NotEqual(string.Empty, vm.FolderError);
    }

    [Fact]
    public void EditingAnExistingClientRuleWithNoCondition_CannotBeResaved()
    {
        // A condition-less Delete could have been saved through this editor between #412 and this
        // guard. Opening one must not let it be saved again unchanged.
        var existing = new MailRule { Name = "Old", Action = RuleAction.Delete, UseFromCondition = true };
        var vm = ServerRuleEditorViewModel.ForEditClient(existing);

        Assert.False(vm.Validate());
        Assert.Equal(ServerRuleEditorViewModel.NoConditionError, vm.ActionsError);
    }
}

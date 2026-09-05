using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using QuickMail.Models;
using QuickMail.ViewModels;
using QuickMail.Views;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Deterministic guard that every free-text condition in the server rule editor is switched by a
/// checkbox (issue #665) — the defect was a condition field with no way to say whether the rule
/// should use it, and the way it comes back is somebody adding the seventh condition field by
/// copying the old label-plus-TextBox shape.
///
/// Register a new condition field in <see cref="Conditions"/> and this suite fails until the
/// checkbox and both gating bindings are there. Mirrors <c>TypeAheadWiringTests</c>' Sites table.
/// </summary>
[Collection("WpfTests")]
public class RuleEditorConditionWiringTests
{
    /// <summary>Binding path of each condition TextBox, and of the switch that gates it.</summary>
    private static readonly (string Field, string Switch)[] Conditions =
    [
        ("FromAddresses",         "UseFromAddresses"),
        ("SubjectContains",       "UseSubjectContains"),
        ("SenderContains",        "UseSenderContains"),
        ("SentToAddresses",       "UseSentToAddresses"),
        ("BodyOrSubjectContains", "UseBodyOrSubjectContains"),
        ("BodyContains",          "UseBodyContains"),
    ];

    // Logical tree, not visual: a Window that is never shown has no visual tree, and the logical one
    // carries every element the XAML declares — including the Advanced expander's, whether or not it
    // is expanded. Bindings are attached at parse time, which is all this test reads.
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static string? PathOf(DependencyObject element, DependencyProperty property) =>
        BindingOperations.GetBinding(element, property)?.Path.Path;

    [StaFact]
    public void EveryConditionFieldIsGatedByItsOwnCheckbox()
    {
        WpfTestHost.EnsureStyles("AccessibleStyles", "ThemedControls");

        var vm = ServerRuleEditorViewModel.ForNew();
        var window = new ServerRuleEditorWindow(vm, new List<AccountModel>(), new Dictionary<Guid, List<MailFolderModel>>());
        try
        {
            var tree = Descendants(window).ToList();

            var switches = tree.OfType<CheckBox>()
                .Select(c => PathOf(c, ToggleButton.IsCheckedProperty))
                .Where(p => p != null)
                .ToHashSet();

            foreach (var (field, gate) in Conditions)
            {
                var box = tree.OfType<TextBox>().SingleOrDefault(t => PathOf(t, TextBox.TextProperty) == field);
                Assert.True(box != null, $"No TextBox bound to '{field}' in the rule editor.");

                Assert.True(switches.Contains(gate),
                    $"'{field}' has no CheckBox bound to '{gate}'. A condition the user cannot switch " +
                    "off is the #665 defect.");

                // Switched off, the field must stop being editable AND leave the tab order. A box that
                // is skipped but still typeable, or typeable but unreachable, is worse than neither.
                Assert.True(PathOf(box!, TextBox.IsReadOnlyProperty) == gate,
                    $"'{field}' does not bind IsReadOnly to '{gate}'.");
                Assert.True(PathOf(box!, Control.IsTabStopProperty) == gate,
                    $"'{field}' does not bind IsTabStop to '{gate}'.");
            }
        }
        finally
        {
            window.Close();
        }
    }
}

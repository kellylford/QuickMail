# The WPF DataTemplate Accessible Name Trap — and Why AI Gets It Wrong

*Posted June 2026*

---

## The Bug

A user filed [issue #107](https://github.com/kellylford/QuickMail/issues/107) against QuickMail's keyboard shortcuts settings screen. When navigating the shortcuts list by arrowing up and down, NVDA reported something like:

> QuickMail.ViewModels.SettingsViewModel+HotkeyRowViewModel  objekt with data  selected  2 of 62

Instead of, say: *"Reply, Mail, Ctrl+R"*.

The user confirmed the same behaviour with Narrator. Interestingly, JAWS did not reproduce the issue at all — it read the command names correctly without any changes on our side.

---

## Why It Happens

When you bind a `ListBox` or `ListView` to a collection of .NET objects using a `DataTemplate`, WPF creates a `ListBoxItem` or `ListViewItem` container for each row. That container is what the UI Automation (UIA) tree exposes to screen readers. The problem is that WPF sets the accessible name of the container by calling `ToString()` on the bound data object.

If your ViewModel doesn't override `ToString()`, you get the default .NET implementation: the fully-qualified class name. For a nested partial class like `HotkeyRowViewModel`, that becomes `QuickMail.ViewModels.SettingsViewModel+HotkeyRowViewModel` — exactly what the user was hearing.

This is not a NVDA bug or a Narrator bug. It is a documented WPF behaviour that Microsoft's own accessibility engineering team wrote about in a blog post titled [*"How To: Get automation working properly on data bound WPF list or combo box"*](https://learn.microsoft.com/en-us/archive/blogs/gautamg/how-to-get-automation-working-properly-on-data-bound-wpf-list-or-combo-box) (Gautam G, MSDN). The post dates from the early WPF era, which means this trap has been catching developers for close to twenty years.

---

## Why JAWS Doesn't Repro

JAWS protects its users from this class of developer error. When JAWS encounters a WPF list item whose UIA accessible name is unhelpful — empty, or a string that looks like a CLR type name — it walks the item's visual subtree and builds a name from the readable text it finds. The user hears the command name instead of the class name, regardless of whether the developer got the labelling right.

This is a deliberate, user-focused decision by Freedom Scientific. The WPF `ToString()` fallback was catching developers often enough and hurting blind users consistently enough that fixing it on the screen reader side was the right call. Glen Gordon, the original architect of JAWS and a Software Fellow at Freedom Scientific, has been deeply involved in these kinds of user-protective decisions throughout his career. Whether he personally drove this specific one I cannot confirm with certainty — our recollection is that there were conversations about this exact WPF pattern with folks at Freedom Scientific — but it has been in JAWS for many years and reflects the kind of thoughtful advocacy for users that Freedom Scientific has long been known for.

NVDA and Narrator don't apply the same compensation — they report what the UIA tree says. That means they surface the developer's mistake rather than working around it, which is how this bug came to light. Neither approach is wrong; they reflect different priorities. JAWS chose to protect its users from a systemic platform failure. NVDA and Narrator surface the failure so it can be fixed at the source.

The practical implication for developers is a testing gap: if you test exclusively with JAWS, this class of labelling mistake won't surface. The right answer is to label controls correctly regardless — and to test with more than one screen reader.

---

## The Two Tempting Wrong Fixes

### 1. Override `ToString()`

The most commonly suggested fix — and the one Microsoft's own old blog post leads with — is to override `ToString()` on the ViewModel:

```csharp
public override string ToString() => $"{Title}, {Category}, {ActiveGesture}";
```

This works. Screen readers start announcing something useful. But it is the wrong tool for the job.

`ToString()` is a general-purpose serialisation method. Its contract is to return a developer-facing string representation of the object — useful in a debugger, in log output, in a `string.Format` call. Bending it to serve as the UIA accessible name conflates two completely separate concerns. It also means that if someone later adds logging that prints `HotkeyRowViewModel` instances, they get screen-reader copy in their log files instead of something debuggable.

### 2. Set `AutomationProperties.Name` on the DataTemplate root

A slightly better approach that also appears in AI-generated code and blog posts: set `AutomationProperties.Name` on the root element inside the `DataTemplate`:

```xml
<DataTemplate>
    <Grid AutomationProperties.Name="{Binding AccessibleName}">
        <!-- ... -->
    </Grid>
</DataTemplate>
```

This is closer to correct, but it has a side effect: the `Grid` becomes visible in the UIA Control view as a distinct element, creating an extra layer in the accessibility tree that screen readers may announce separately. You then need to suppress it with `AutomationProperties.AccessibilityView="Raw"` on the same `Grid` — and now you have a two-property workaround on an inner element when the real fix belongs on the container.

---

## The Correct Fix

Set `AutomationProperties.Name` on the **item container** — `ListBoxItem` or `ListViewItem` — using `ItemContainerStyle`:

```xml
<ListView ItemsSource="{Binding HotkeyRows}" ...>
    <ListView.ItemContainerStyle>
        <Style TargetType="ListViewItem">
            <Setter Property="AutomationProperties.Name" Value="{Binding AccessibleName}"/>
        </Style>
    </ListView.ItemContainerStyle>
    ...
</ListView>
```

And on the ViewModel, add a dedicated property for the accessible label:

```csharp
public string AccessibleName
{
    get
    {
        var gesture = string.IsNullOrEmpty(ActiveGesture) ? "no shortcut" : ActiveGesture;
        return $"{Title}, {Category}, {gesture}";
    }
}
```

The name lives exactly where the UIA tree expects it — on the container element — and it updates when the shortcut changes (via `[NotifyPropertyChangedFor(nameof(AccessibleName))]` on the underlying gesture property). The `DataTemplate` internals don't need to know about accessibility at all. The ViewModel surface is clean. `ToString()` remains available for debugging.

This is also exactly the pattern WPF itself uses for controls that work correctly: `AutomationProperties.Name` on the outermost focusable element.

---

## It's Everywhere — Including Microsoft's Own Code

This isn't a niche edge case. A quick search of large Microsoft GitHub repositories turns up the pattern in prominent, actively-maintained projects.

### microsoft/WPF-Samples — the official WPF sample gallery

[`WPFGallery/Views/Collections/ListViewPage.xaml`](https://github.com/microsoft/WPF-Samples/blob/master/Sample%20Applications/WPFGallery/Views/Collections/ListViewPage.xaml) is the page developers land on when they search for how to use `ListView` in WPF. It contains three `ListView` examples — a basic list, a list with selection modes, and a `GridView` — all bound to a collection of `Person` data objects via `DataTemplate`. None of them set `AutomationProperties.Name` on the `ListViewItem` container.

```xml
<ListView
    ItemsSource="{Binding ViewModel.BasicListViewItems, Mode=TwoWay}">
    <ListView.ItemTemplate>
        <DataTemplate DataType="{x:Type models:Person}">
            <TextBlock Margin="8,4" Text="{Binding Name, Mode=OneWay}" />
        </DataTemplate>
    </ListView.ItemTemplate>
</ListView>
```

The `Person` type is a C# `record`. Records auto-generate a `ToString()` that produces something like:

> `Person { FirstName = Lucas, LastName = Hudson, Name = Lucas Hudson, Company = CrestWave Dynamics }, 5 of 50, selected`

That is what Narrator actually announces when arrowing through the list. It is readable — C# records accidentally save you from the worst-case failure (a bare class name) — but it is verbose, it exposes internal field structure the user never asked for, and it is entirely dependent on an implementation detail of the data type rather than a deliberate accessibility decision. JAWS announces the same thing; its visual-subtree heuristic does not kick in when the `ToString()` output is already textual rather than a type name.

The GridView example on the same page compounds the problem — each column of each row reads out the full record dump rather than just the cell value.

Developers copying this sample are learning the wrong pattern from a source that is supposed to be authoritative, and it is broken enough to pass casual testing — the output is readable, so most developers won't notice anything is wrong.

### microsoft/WPF-Samples — the DataGrid sample (a worse case)

The WPFGallery also ships a DataGrid sample bound to a `Product` collection. Unlike `Person`, `Product` is a plain class with no meaningful `ToString()`. Arrowing across cells in the QuantityPerUnit column, Narrator announces:

> `Item: WPFGallery.Models.Product, Column Display Index: 3, data grid cell, Column Header QuantityPerUnit, Column 4 of 6`

The cell value is completely absent. The user hears the class name instead of the data.

This case is more severe than the ListView case for two reasons. First, the fallback is the bare class name rather than a readable record dump — there is no accidental mitigation. Second, **JAWS's visual-subtree heuristic does not protect DataGrid cells** the way it protects ListView items. The UIA structure of a DataGrid cell is different enough that JAWS cannot synthesize a name from the subtree, so all three screen readers hit the same broken announcement. There is no safety net here at all.

### microsoft/PowerToys — the PowerLauncher (Run) result list

[`PowerLauncher/ResultList.xaml`](https://github.com/microsoft/PowerToys/blob/main/src/modules/launcher/PowerLauncher/ResultList.xaml) is the WPF `ListView` that displays search results in PowerToys Run. Its `ItemContainerStyle` points to `ResultsListViewItemStyle`, defined in [`Styles/Styles.xaml`](https://github.com/microsoft/PowerToys/blob/main/src/modules/launcher/PowerLauncher/Styles/Styles.xaml). That style completely replaces the `ControlTemplate` — and sets no `AutomationProperties.Name`:

```xml
<Style x:Key="ResultsListViewItemStyle" TargetType="{x:Type ListViewItem}">
    <Setter Property="Foreground" Value="{DynamicResource ListViewItemForeground}" />
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="Margin" Value="0,0,0,2" />
    <Setter Property="Padding" Value="4" />
    <Setter Property="OverridesDefaultStyle" Value="True" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="{x:Type ListBoxItem}">
                <!-- visual chrome only — no AutomationProperties.Name -->
                <Border ...>
                    <ContentPresenter Margin="{TemplateBinding Padding}" />
                </Border>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

What screen readers announce when arrowing through PowerToys Run results depends entirely on what the `Result` data class's `ToString()` returns. Whether JAWS's subtree heuristic compensates here depends on the UIA structure of the result items — untested, but the labelling is unintentional regardless.

---

## The AI Training Problem

The user who filed this bug made an astute observation: "This bug shows the problem with AI training."

They are right. AI code assistants — including the one that helped fix this issue — almost universally suggest `ToString()` overrides or `DataTemplate`-root approaches when asked how to fix WPF screen reader labelling. The reason is straightforward: the training corpus is full of those solutions. They are in old MSDN blog posts, Stack Overflow answers, GitHub issues, and published books from 2008–2015. They are common. They work *enough* that developers ship them and close the issue.

The correct pattern — `ItemContainerStyle` with `AutomationProperties.Name` on the container — appears far less often, because:

1. Developers who get the `ToString()` fix to work stop looking.
2. They test with JAWS, which protects its users from this exact developer mistake — so the symptom never appears, and there is no signal that anything is wrong.
3. The `ItemContainerStyle` approach requires knowing WPF's UIA layering well enough to understand *why* the container is the right target.

What the AI assistant produced in an earlier draft of this fix was the `ToString()` override. It was confident. It cited plausible reasons. And it was the wrong tool, precisely because those reasons appear so frequently in the training data.

The broader lesson is not that AI assistants are useless for accessibility work. It is that for any pattern where a popular-but-wrong solution exists alongside a correct-but-obscure one, and where a major screen reader masks the problem so testing doesn't catch it, the AI is likely to suggest the wrong solution with high confidence. Knowing *why* something is wrong requires understanding that screen readers behave differently, that JAWS has compensations NVDA does not, and that `ToString()` and `AutomationProperties.Name` serve different masters.

That kind of contextual reasoning — "this solution is common, but it's common because a major tool hides the bug" — is exactly what is absent from training signal derived from shipped code.

---

## Summary

| Approach | Works in JAWS? | Works in NVDA/Narrator? | Correct? |
|---|---|---|---|
| No label (default) | ✅ (JAWS protects users) | ❌ reads class name | ❌ developer error |
| Override `ToString()` | ✅ | ✅ | ❌ wrong tool |
| `AutomationProperties.Name` on DataTemplate root | ✅ | ✅ | ⚠️ extra UIA node |
| `AutomationProperties.Name` via `ItemContainerStyle` | ✅ | ✅ | ✅ |

The fix landed in [PR #108](https://github.com/kellylford/QuickMail/pull/108) and is included in the v0.7.5 release notes.

---

*QuickMail is a keyboard-centric WPF email client. Accessibility is a first-class concern.*

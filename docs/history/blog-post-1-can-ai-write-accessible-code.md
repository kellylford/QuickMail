# Can AI Write Accessible Code? The QuickMail Experiment

**Draft for theideaplace.net — by Kelly Ford**
**Status: Ready for review**

---

I've been experimenting with AI-assisted development for about a year now. WeatherFast, Sports Scores, RSS Quick, the Image Description Toolkit — each project taught me something about what AI can and can't do. But QuickMail is the most ambitious test yet: a full desktop email client for Windows, built primarily through AI, with accessibility as the foundation.

Not an afterthought. Not a compliance checkbox. The foundation.

Here's what I learned.

## The State of Email Accessibility Is Bad

Before building anything, I needed to understand what already exists. I use email every day. I use a screen reader every day. I know what's broken. But I wanted to be systematic about it.

The competitive analysis confirmed what I already knew from experience: no major Windows email client delivers a genuinely accessible experience.

Outlook has the most features and the worst accessibility consistency. The screen reader experience varies by version, update, and which pane has focus. I've written about Microsoft's accessibility failures before — Copilot serving half-answers, Notepad tables without screen reader announcements, raw class names in chat output. Outlook is more of the same.

Thunderbird is open source, which means anyone can fix accessibility bugs. It also means nobody is paid to fix them. Bugs sit for years.

Windows Mail is a web wrapper in a desktop shell. All the accessibility problems of web apps plus the performance problems of running a browser engine for what should be a native app.

Gmail is the best of the web options. Still a web app. Google ships breaking accessibility changes without notice.

eM Client and Mailbird use proprietary UI frameworks with poor UIA support. Nearly unusable with a screen reader.

The gap was clear: build the email client that screen reader users recommend to each other.

## What QuickMail Gets Right

The prototype — built over several months with AI assistance — established the foundation. Here's what matters:

**UIA notifications, not ARIA hacks.** WPF's `AutomationProperties.LiveSetting` fires UIA `LiveRegionChanged`, which screen readers only support inside web browsers. For desktop apps, the correct API is `RaiseNotificationEvent`. QuickMail uses it. Every announcement goes through `AccessibilityHelper.Announce()` with proper category filtering — Hint, Status, or Result. Users can toggle each category independently.

This matters because screen reader users get flooded with verbose output from most apps. QuickMail lets you silence "Press Escape to return to the list" after you've learned it, while keeping "3 messages found" results.

**Status bar as a proper UIA StatusBar.** Four named regions navigable with Left and Right arrows. Each region has proper `AutomationProperties.Name`. The Rules region is a real Button with `InvokePattern` — not a styled TextBox that announces as "edit." This is the kind of detail that separates real accessibility from checkbox accessibility.

**Focus management that works.** F6 cycles through all panes. Ctrl+0 through Ctrl+3 and Ctrl+9 jump directly. Escape closes the reading pane and returns to the message list. Focus is restored after folder loads. The WebView2 reading pane has retry logic for focus handoff.

**No raw class names in the UI.** Unlike Microsoft's Copilot app which announces `CopilotNative.Chat.Controls.ViewModels.MessageThinkingAndActivityPart`, QuickMail's `AutomationProperties` are human-readable strings.

**Keyboard customization that's transparent.** The Command Registry plus `hotkeys.json` system means users can remap any command. The Settings dialog shows conflicts. This is better than Thunderbird's hidden key config and Outlook's ribbon-only customization.

## What AI Got Wrong

This is the honest part. AI wrote most of the code. It also introduced bugs that a human would catch.

**The hotkey customization was silently broken.** User-customized hotkeys saved correctly to `hotkeys.json` but were ignored on next launch. The lookup code read legacy integer fields that were always zero for new bindings, instead of parsing the `Gesture` string. The settings dialog appeared to work. The file saved correctly. But the keys did nothing. This was a P0 bug that made an entire feature — a key differentiator — non-functional.

**The spell check implementation took three tries.** The WPF `SpellCheck` API has naming conflicts with the XAML property of the same name. The AI tried `GetNextSpellingErrorPosition` (doesn't exist), then `SpellCheck.GetSpellingError` (name conflict), then finally `TextBox.GetSpellingError` (correct). Each cycle required a full build. A human developer would have checked the API documentation first.

**The ICS parser returned the wrong value for Organizer.** When parsing calendar invites, the `Organizer` field was returning the display name ("John Doe") instead of the email address ("john@example.com"). The Test Enforcer agent caught this. Without it, accepting a calendar invite would have sent the reply to "John Doe" instead of an email address.

**The tutorial's final step was broken.** Step 6 teaches the Escape key. But Escape was hardcoded to cancel the tutorial before checking whether the current step expected Escape. So step 6 could never be completed — pressing Escape always cancelled. The Test Enforcer caught this too.

**The sort parser had a case-sensitivity bug.** After centralizing the sort string-to-enum mapping, the code used `.ToLowerInvariant()` but compared against mixed-case strings like `"dateAsc"`. Lowercased, that's `"dateasc"` — which doesn't match. Every saved view with a non-default sort order was broken.

## What This Means

AI can implement accessibility patterns correctly. `RaiseNotificationEvent`, `AutomationProperties.Name`, `ControlType`, `InvokePattern` — these are well-documented APIs. AI models trained on MSDN get them right.

AI needs explicit accessibility rules. Without instructions like "no MessageBox in ViewModels" and "every Announce() call must have a category," AI produces inaccessible code. The rules in `CLAUDE.md` are essential.

AI doesn't know what "feels accessible." It doesn't know that a screen reader user needs to hear "3 unread messages from Kelly Ford" not just "3 items." The PM specs, the acceptance criteria, the accessibility requirements — these require human experience.

The bottleneck is coordination, not capability. Individual tasks — write spec, implement feature, write tests — are all within AI capability. The challenge is orchestrating them in the right order with the right handoffs. When I did everything myself, tests weren't written and bugs slipped through. When I used the crew model — PM agent writes spec, Dev agent implements, Test agent verifies — the Test agent found real bugs every single time.

## The Bottom Line

Can AI write accessible code? Yes — with the right instructions, the right process, and human review. The code it produces follows accessibility patterns more consistently than most human-written code I've reviewed. But it also introduces bugs that a human would catch immediately.

The question isn't whether AI can write accessible code. It's whether we build the processes around AI to catch what it misses. The feature crew model — PM, Dev, Test, with a human in the loop — is one answer. It produced 341 passing tests and caught five real bugs across five features.

QuickMail isn't done. But it's further along than I expected, and the process of building it has taught me more about AI and accessibility than any blog post or conference talk ever could.

---

*QuickMail is open source at [github.com/kellylford/QuickMail](https://github.com/kellylford/QuickMail). The roadmap branch contains all the work described in this post.*

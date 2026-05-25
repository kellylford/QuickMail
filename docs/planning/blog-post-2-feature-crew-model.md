# The Feature Crew Model: PM, Dev, and Test AI Agents Building Software Together

**Draft for theideaplace.net — by Kelly Ford**
**Status: Ready for review**

---

When I started QuickMail, I had a question: can AI build accessible software? The answer turned out to be yes — but only with the right process. The process that worked was something I'm calling the Feature Crew Model.

## The Problem with Solo AI Development

My early experiments with AI-assisted development followed a simple pattern: I'd describe what I wanted, the AI would write code, I'd test it with a screen reader, and we'd iterate. This worked for small projects. WeatherFast, RSS Quick — these are focused apps with clear boundaries.

QuickMail is different. It's a full email client. IMAP, SMTP, SQLite, WebView2, OAuth2, MIME parsing, conversation threading, mail rules, saved views, keyboard customization, UIA notifications. The codebase is over 15,000 lines across dozens of files. A single AI session can't hold all of that in context.

When I tried the solo approach on QuickMail, I got results — but they were inconsistent. Features shipped without tests. Bugs slipped through. The spell check implementation took three tries because the AI kept guessing API names instead of checking documentation. I was acting as PM, developer, tester, and reviewer all at once, and I was missing things.

## The Crew Model

The insight came from how real software teams work. You don't have one person do everything. You have a PM who writes the spec, a developer who implements it, a tester who verifies it, and a reviewer who signs off. Each role catches different kinds of problems.

What if AI agents could fill those roles?

Here's how it works:

**PM Agent** writes the product specification. User stories, acceptance criteria, accessibility requirements, keyboard shortcut assignments, files to create and modify. The spec is the contract. If it's not in the spec, it doesn't get built.

**Dev Agent** implements the feature. It reads the spec, reads the relevant existing code for context, makes changes across all the files, and verifies the build. It follows the rules in `CLAUDE.md` — MVVM discipline, CommandRegistry registration, `AccessibilityHelper.Announce()` with proper categories.

**Test Agent** verifies the implementation. It reads the spec and the changed code, writes unit tests, XAML parse tests, and ViewModel construction tests. It runs the full test suite. If anything fails, it reports back with exactly what broke.

**Human Reviewer** (me) does the final check. Does it actually work with a screen reader? Does the focus management feel right? Are the announcements helpful or noisy? AI can implement accessibility patterns. Only a screen reader user can verify that they actually work.

## What the Crews Built

Over the course of the QuickMail roadmap, five crews shipped five features:

**Crew Alpha — Foundation Fixes.** The P0 bugs from the engineering review: hotkey customization was silently broken, `BatchObservableCollection` could deadlock, `ContactService` had race conditions. These were fixed before the roadmap work began.

**Crew Bravo — ICS Calendar Handling.** Parse calendar invites from incoming messages, display an event card in the reading pane, accept/decline/tentative with ICS reply generation. The Test Agent found that the `Organizer` field was returning the display name instead of the email address — a bug that would have broken every calendar reply.

**Crew Charlie — First-Run Accessibility Tutorial.** An interactive overlay that teaches new users the six essential keyboard shortcuts by having them press each one. The Test Agent found that step 6 (Escape) could never be completed because Escape was hardcoded to cancel the tutorial first.

**Crew Delta — Message Templates.** Save common responses as templates with placeholder support (`{sender}`, `{date}`, `{time}`). Insert with a searchable picker dialog. The Test Agent found that the `{sender}` placeholder returned empty when the account had a username but no display name.

**Crew Echo — Phase 5 Refactoring.** Extract repeated patterns from `MainViewModel` (3,355 lines) and `MainWindow.xaml.cs` (3,010 lines). Centralize ViewMode and Sort serialization. Fix CTS disposal leaks. The Test Agent found that the centralized sort parser had a case-sensitivity bug — `.ToLowerInvariant()` was comparing against mixed-case strings.

## The Pattern

Every crew followed the same pattern. And every crew's Test Agent found a real bug that the Dev Agent missed. Not theoretical edge cases — bugs that would have affected real users.

The ICS organizer bug would have sent calendar replies to "John Doe" instead of "john@example.com." The tutorial Escape bug would have made the final step impossible to complete. The template placeholder bug would have produced blank sender names. The sort parser bug would have broken every saved view with a non-default sort order.

These are exactly the kinds of bugs that slip through when one person — or one AI — does everything. The crew model catches them because each agent looks at the work from a different angle.

## Why This Matters for Accessibility

Accessibility bugs are especially vulnerable to the solo development problem. A developer implementing a feature is thinking about functionality. They're not thinking about what a screen reader will announce when focus lands on a control. They're not thinking about whether the tab order makes sense without a mouse. They're not thinking about whether the UIA notification category is correct.

A PM spec that explicitly lists accessibility requirements — `AutomationProperties.Name` on every control, `AnnouncementCategory` on every announcement, keyboard-only operability — forces the Dev Agent to think about these things during implementation. And a Test Agent that verifies those requirements catches what slips through.

The result is software where accessibility isn't an afterthought. It's baked into the spec, the implementation, and the verification.

## The Numbers

| Metric | Value |
|--------|-------|
| Features shipped by crews | 5 |
| Bugs found by Test Agents | 5 (one per crew) |
| Tests at start | 273 |
| Tests at end | 341 |
| New test files created | 6 |
| PM specs written | 5 |
| Build warnings | 0 |
| Build errors | 0 |

## What's Next

The crew model worked for QuickMail. I think it would work for other projects too. The key ingredients are:

1. **Detailed PM specs with explicit accessibility requirements.** The AI can't guess what "accessible" means. You have to tell it.

2. **Clear rules encoded in instructions files.** `CLAUDE.md` and `copilot-instructions.md` are the constitution. Every agent reads them.

3. **Separate agents for separate roles.** The Dev Agent and Test Agent need to be different invocations with different prompts. They catch different things.

4. **Human review at the end.** AI can implement UIA patterns. It can't tell you whether the experience feels right with a screen reader.

I'm not claiming this is the final answer. But it's the best process I've found so far for building accessible software with AI assistance. And the results — 341 tests, zero warnings, five real bugs caught — speak for themselves.

---

*QuickMail is open source at [github.com/kellylford/QuickMail](https://github.com/kellylford/QuickMail). The roadmap branch contains all the work described in this post, including the PM specs, implementation, and tests for every feature crew.*

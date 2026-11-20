# The QuickMail Story: Building an Accessible Email Client with AI

**A project by Kelly Ford, The Idea Place**
**Documenting the journey from prototype to product**

---

## Prologue: Why QuickMail Exists

I'm a screen reader user. I've been one for decades. I've watched Microsoft, Google, Mozilla, and countless others ship email clients that range from "mostly works if you memorize the quirks" to "completely unusable."

In 2025-2026, I started experimenting with AI-assisted development. Not just asking ChatGPT for code snippets — actually building real applications. WeatherFast. Sports Scores. RSS Quick. The Image Description Toolkit. Each project taught me something about what AI can and can't do.

QuickMail is the most ambitious test yet: a full desktop email client for Windows, built primarily through AI-assisted development, with accessibility as the foundation — not an afterthought.

This document tells the story of that build. It's honest about what worked, what didn't, and what we learned about building accessible software with AI.

---

## Chapter 1: The State of Email Accessibility (Spoiler: It's Bad)

Before building QuickMail, I needed to understand the competitive landscape. Not from a features perspective — from an accessibility perspective. Here's what I found:

### Outlook (Classic)
Microsoft's flagship email client has the most features. It also has the worst accessibility consistency. The screen reader experience varies dramatically by version, update, and which pane has focus. The reading pane randomly loses focus. The message list announces wrong item counts. The calendar is a disaster. And Microsoft — the company that talks endlessly about accessibility — ships regressions constantly.

I've written about this. The Windows Copilot app serves half-answers to screen reader users. Notepad got table support without screen reader announcements. Copilot's chat announces raw class names like `CopilotNative.Chat.Controls.ViewModels.MessageThinkingAndActivityPart`. This is the company that's supposed to be leading on accessibility.

### Thunderbird
Open source means anyone can fix accessibility bugs. It also means nobody is paid to fix them. The folder pane doesn't announce expand/collapse state reliably. The filter dialog is a maze of unlabeled controls. Bugs sit for years.

### Windows Mail / New Outlook
A web wrapper in a desktop shell. All the accessibility problems of web apps — focus trapping, ARIA misbehavior, browse mode vs focus mode confusion — plus the performance problems of running a browser engine for what should be a native app.

### Gmail (Web)
The best of the web options. But still a web app. Google ships breaking accessibility changes without notice. Keyboard shortcuts conflict with screen reader commands. Conversation view is confusing.

### The Others
eM Client, Mailbird — proprietary UI frameworks with poor or nonexistent UIA support. Nearly unusable with a screen reader.

### The Gap
No major Windows email client delivers a genuinely accessible experience. Every one of them treats accessibility as a compliance checkbox, not as a fundamental design requirement. The opportunity was clear: build the email client that screen reader users recommend to each other.

---

## Chapter 2: The Prototype (v0.1 — v0.6.3)

QuickMail started as a prototype. The goal was simple: prove that a keyboard-first, screen-reader-friendly email client was possible on Windows using WPF and .NET 8.

### What We Built

The prototype established the foundation:

- **Multi-account IMAP/SMTP** with pooled connections and IMAP IDLE push
- **Three-pane layout** with F6 cycling and direct pane focus (Ctrl+0-3)
- **UIA notifications** via `RaiseNotificationEvent` — the correct API for desktop screen reader announcements
- **Category-filtered announcements** — users can toggle Hint, Status, and Result announcements independently
- **Command Registry** — all keyboard shortcuts registered and user-customizable
- **Command Palette** (Ctrl+Shift+P) — searchable list of all commands
- **Status bar with arrow-key navigation** — four named regions, proper UIA StatusBar pattern
- **WebView2 reading pane** with strict CSP and simplified reader mode
- **Mail rules** — client-side automatic actions on incoming messages
- **Saved views** — persistent filtered views of messages
- **Conversation view, From view, To view** — multiple ways to group messages
- **Virtual folders** — All Mail, All Inboxes, All Drafts, All Sent, All Trash
- **OAuth2 for Microsoft accounts**
- **Profile support** — separate data directories for work/personal

### What We Learned

1. **AI can implement UIA correctly.** `RaiseNotificationEvent`, `AutomationProperties.Name`, `ControlType`, `InvokePattern` — these are well-documented APIs. AI models trained on MSDN get them right.

2. **AI needs explicit accessibility rules.** Without instructions like "no MessageBox in ViewModels" and "every Announce() call must have a category," AI will produce inaccessible code. The rules in `CLAUDE.md` and `copilot-instructions.md` are essential.

3. **The bottleneck is human judgment, not AI capability.** AI doesn't know what "feels accessible." It doesn't know that a screen reader user needs to hear "3 unread messages from Kelly Ford" not just "3 items." The PM specs, the acceptance criteria, the accessibility requirements — these require human experience.

4. **Code review catches what AI misses.** The May 21 engineering review found real bugs: hotkey customization was silently broken, `BatchObservableCollection` could deadlock, `ContactService` had race conditions. AI wrote the code; human review found the problems.

---

## Chapter 3: The Roadmap (May 2026 — )

The prototype proved the concept. Now we need to build the product. The roadmap is organized into five phases:

### Phase 1: Foundation Fixes ✅ (Completed before roadmap)
All P0/P1 bugs from the May 21 review were addressed before the roadmap branch was created. The codebase is stable.

### Phase 2: Table Stakes (Current)
Features every email client must have:
- **Spell check** — with screen reader announcements for misspellings
- **Email signatures** — plain-text and HTML, per account
- **Calendar invite handling (ICS)** — parse, display, accept/decline
- **Message list accessibility** — consistent, informative announcements

### Phase 3: Differentiators
Features that make QuickMail better than competitors for screen reader users:
- **First-run accessibility tutorial** — interactive, self-voicing
- **Audio feedback** — sounds for send/receive/error
- **Message read aloud** — TTS for quick triage
- **Contact manager UI** — proper add/edit/delete/search

### Phase 4: Power User
Features for advanced users:
- **PGP/GPG encryption**
- **Message templates**
- **Server-side search**
- **Scheduled send**

### Phase 5: Refactoring
Technical improvements for sustainability:
- Extract repeated patterns from MainViewModel and MainWindow
- Proper SQLite migration system
- CTS disposal fixes

---

## Chapter 4: The Feature Crew Model

Each feature is built by a crew of three AI agents:

### PM Agent
Writes the product specification:
- User stories and personas
- Accessibility requirements (WCAG 2.2 AA minimum)
- Acceptance criteria
- Competitive context
- Keyboard shortcut assignments

### Dev Agent
Implements the feature:
- Follows MVVM rules strictly
- Registers all shortcuts in CommandRegistry
- Uses AccessibilityHelper.Announce() with proper categories
- No MessageBox/Window/Dispatcher in ViewModels
- Produces working, compilable code

### Test Agent
Verifies the implementation:
- Unit tests for services and ViewModels
- XAML parse tests (STA thread)
- ViewModel construction tests with stubs
- Accessibility assertions (AutomationProperties, UIA patterns)

### The Human in the Loop
After all three agents complete their work, a human (Kelly) reviews:
- Does it actually work with a screen reader?
- Does the focus management feel right?
- Are the announcements helpful or noisy?
- Is the keyboard flow natural?

AI can implement accessibility patterns. Only a screen reader user can verify that they actually work.

---

## Chapter 5: What We're Proving

QuickMail is an experiment in several questions:

### Can AI build production-quality accessible software?
The answer so far is: yes, with the right instructions and human review. AI gets the APIs right. It follows patterns consistently. It doesn't get tired and skip the `AutomationProperties.Name` on the 20th control. But it needs explicit rules and human verification.

### Can AI maintain accessibility discipline across a large codebase?
The `CLAUDE.md` and `copilot-instructions.md` files encode the rules. Every AI agent that touches the codebase reads these rules. The result is consistent accessibility patterns across thousands of lines of code written by different AI models at different times.

### Is "vibe coding" compatible with accessibility?
Vibe coding — describing what you want and letting AI implement it — works for accessibility IF the descriptions include accessibility requirements. "Add a delete button" produces an inaccessible button. "Add a delete button with AutomationProperties.Name='Delete message', keyboard shortcut Delete, and UIA notification 'Message deleted' on success" produces an accessible one.

### Can a small team (or one person) build a competitive product with AI?
The QuickMail prototype was built primarily by one person working with AI assistants. It has features that took teams of developers years to build in Outlook and Thunderbird. The IMAP connection pooling, the Command Registry, the UIA notification system — these are sophisticated engineering. AI made them possible at a scale that would otherwise require a team.

---

## Chapter 6: The Blog Posts

These posts will be published on [theideaplace.net](https://theideaplace.net) as the project progresses:

### Post 1: "Can AI Write Accessible Code?" (Planned)
The QuickMail experiment: what we built, how we built it, what worked, what didn't. Honest assessment of AI's strengths and weaknesses in accessibility implementation.

### Post 2: "Accessibility by Specification" (Planned)
How detailed PM specs with explicit accessibility requirements produce better AI-generated code. The difference between "add a settings dialog" and "add a settings dialog where every control has AutomationProperties.Name, TabIndex is logical, and state changes are announced via UIA Notification."

### Post 3: "The Feature Crew Model" (Planned)
PM/Dev/Test AI agents collaborating on accessibility-first features. How the three-agent model catches issues that a single agent misses. The importance of the human review step.

### Post 4: "What Screen Reader Users Actually Need from Email" (Planned)
Based on the competitive analysis and QuickMail's design decisions. Why Outlook fails, why Thunderbird isn't the answer, and what "accessible email" actually means.

---

## Appendix: The Numbers

| Metric | Value |
|--------|-------|
| Lines of code in prototype | ~15,000+ |
| AI-written code | >90% |
| Human-written code | Architecture, rules, specs, review |
| Accessibility-specific code | ~500 lines (AccessibilityHelper, UIA patterns, announcements) |
| Registered commands | 25+ |
| Test files | 15+ |
| P0 bugs found in review | 6 (all fixed) |
| Features shipped in prototype | 20+ |
| Features planned in roadmap | 20 |

---

*This document is maintained as part of the QuickMail roadmap initiative. It will be updated as features ship and the story evolves.*

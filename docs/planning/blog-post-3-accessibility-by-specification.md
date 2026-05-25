# Accessibility by Specification: How Detailed Requirements Produce Better AI-Generated Code

**Draft for theideaplace.net — by Kelly Ford**
**Status: Ready for review**

---

There's a pattern I've noticed across years of writing about accessibility failures. Whether it's Fidelity's credit card website jamming all transaction data into a single ARIA label, or Microsoft's Copilot announcing raw class names in chat output, or Notepad getting table support without screen reader announcements — the root cause is almost always the same.

Nobody specified what "accessible" means.

Not in the user story. Not in the acceptance criteria. Not in the definition of done. Accessibility gets treated as a vague aspiration — "the app should be accessible" — rather than a set of concrete, verifiable requirements.

AI-assisted development makes this problem worse. If you tell an AI "add a delete button," you get a button. It might not have an `AutomationProperties.Name`. It might not have a keyboard shortcut. It might not announce anything when pressed. The AI did exactly what you asked. You just didn't ask for accessibility.

The solution is what I call Accessibility by Specification.

## What Accessibility by Specification Looks Like

Here's a real example from the QuickMail ICS calendar handling feature. The PM spec included these accessibility requirements:

```
- AutomationProperties.Name on every button: "Accept invitation",
  "Tentatively accept invitation", "Decline invitation"
- Event card has AutomationProperties.Name set to the IcsModel.DisplaySummary
- On focus, announce via AccessibilityHelper.Announce() with
  AnnouncementCategory.Result
- Tab order: message body → event card (if present) → attachments
```

These are concrete. Verifiable. The Dev Agent can implement them. The Test Agent can verify them. There's no ambiguity about what "accessible" means.

Compare that to what most teams write:

```
- The feature should be accessible
- Follow WCAG 2.2 AA guidelines
```

That's not a specification. That's a wish. The AI — or the human developer — has to guess what "accessible" means in this specific context. And they'll guess wrong.

## The Difference It Makes

Across five feature crews on QuickMail, every PM spec included explicit accessibility requirements. Every Dev Agent implemented them. Every Test Agent verified them. The result: 341 tests, zero accessibility regressions, and features that work with a screen reader from day one.

Here's what the alternative looks like. When I implemented spell check and email signatures myself — without a formal PM spec — I didn't write tests. The spell check took three build-fix cycles because I was guessing API names. The signature auto-insertion worked but I didn't verify the edge case where an account has a username but no display name. The Test Agent found that bug later.

The spec is the difference between "it probably works" and "it works, and here are the tests that prove it."

## What Goes in an Accessibility Spec

Based on what worked for QuickMail, here's what every feature spec should include:

**1. AutomationProperties.Name on every interactive control.** Not "the button should have a name." The exact string. "Accept invitation." "Delete message." "Search folders." The Dev Agent copies it verbatim. The Test Agent asserts it.

**2. AnnouncementCategory on every UIA notification.** Hint, Status, or Result. This matters because QuickMail lets users toggle each category independently. A "Press Escape to return" hint should be silenceable. A "Message sent" result should not.

**3. Keyboard-only operability.** Every dialog must be fully navigable with Tab, Enter, and Escape. Every action must have a keyboard shortcut or be reachable via the Command Palette. If a feature requires a mouse, the spec is incomplete.

**4. Tab order.** Explicitly state the expected tab sequence. "From combo → To field → Cc field → Bcc field → Subject field → Attachments button → Body → Send button." The Dev Agent sets `TabIndex`. The Test Agent verifies.

**5. Focus management.** Where does focus land when the dialog opens? Where does it go after an action completes? What does Escape do? These are the details that make an app feel responsive versus disorienting.

**6. Screen reader announcement text.** Not just "announce success." The exact text. "Template 'Meeting Follow-up' inserted." "3 messages moved to Archive." "No more misspellings found." The Dev Agent copies it. The Test Agent asserts it.

## Why AI Needs This More Than Humans Do

A human developer with accessibility experience might fill in the gaps. They know that buttons need names. They know that state changes should be announced. They've used a screen reader, or worked with someone who has.

An AI doesn't have that experience. It knows the APIs — `AutomationProperties.Name`, `RaiseNotificationEvent`, `InvokePattern` — because they're well-documented. But it doesn't know when to use them unless you tell it.

This is actually an advantage. A human developer might think "I'll add the accessibility labels later" and then forget. An AI, given explicit requirements, will implement them consistently across every control in every file. It doesn't get tired. It doesn't cut corners. It follows the spec.

The spec just has to be good.

## The Hardest Part

The hardest part of Accessibility by Specification isn't writing the specs. It's knowing what to put in them. That requires experience with screen readers. It requires understanding what information a screen reader user needs at each moment. It requires knowing the difference between a helpful announcement and a noisy one.

This is where the human in the loop is irreplaceable. AI can implement `AutomationProperties.Name`. It can't tell you whether "3 items" or "3 unread messages from Kelly Ford and Sarah Chen" is the right thing to announce when you open a folder. That judgment comes from experience.

The PM specs I wrote for QuickMail drew on years of using email with a screen reader. I know what Outlook gets wrong. I know what Thunderbird gets wrong. I know what information I want to hear and what I want to silence. The specs encoded that knowledge into requirements the AI could follow.

## The Bottom Line

AI can write accessible code. But only if you tell it what "accessible" means — in concrete, verifiable terms. "The app should be accessible" is not a specification. "Every button must have AutomationProperties.Name set to a human-readable string, and every state change must be announced via AccessibilityHelper.Announce() with the correct AnnouncementCategory" is.

The more precise the spec, the better the code. This is true for human developers too. But it's especially true for AI, which will follow the spec exactly — gaps and all.

Accessibility by Specification isn't a new idea. It's what accessibility advocates have been asking for forever: make accessibility requirements concrete, verifiable, and part of the definition of done. AI-assisted development just makes the consequences of vague requirements more visible.

---

*QuickMail is open source at [github.com/kellylford/QuickMail](https://github.com/kellylford/QuickMail). The PM specs for every feature are in the `docs/planning/` directory on the roadmap branch.*

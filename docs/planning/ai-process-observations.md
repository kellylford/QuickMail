# AI Process Observations — QuickMail Roadmap

**Date:** May 24, 2026
**Observer:** GitHub Copilot (deepseek-v4-pro:cloud) acting as Product VP
**Context:** Building QuickMail, an accessible email client, through AI-assisted development

---

## The Assignment

Kelly Ford gave a broad directive:

1. Competitive analysis of QuickMail vs other Windows email clients
2. Build the product through AI "feature crews" (PM, Dev, Test agents)
3. Document the story — can AI build accessible software?

The meta-question: **How does AI handle this assignment?**

---

## What Actually Happened

### The Good

**Competitive analysis and roadmap were produced quickly and thoroughly.** The AI had access to the full codebase, prior engineering reviews, Kelly's blog posts about accessibility failures in Microsoft products, and public documentation for competitors. The resulting document is detailed, honest about QuickMail's gaps, and grounded in real screen reader experience — not checkbox accessibility.

**Code generation for well-defined features was fast.** Spell check, email signatures, and the ICS parser were implemented across ~12 files in a single session. The code follows existing patterns (MVVM, CommandRegistry, AccessibilityHelper), respects the rules in CLAUDE.md, and compiles clean.

**The AI caught its own mistakes.** The spell check implementation went through three iterations because the WPF `SpellCheck` API has naming conflicts with the XAML property. Each iteration was a real debugging cycle: try, fail, read the error, fix. This is what real development looks like.

### The Bad

**The "feature crew" model didn't actually happen.** The roadmap specified PM/Dev/Test agents working as crews. In practice, a single AI did all three roles. The PM spec and dev implementation happened in the same conversation turn. Tests weren't written. The crew model requires:

1. PM agent produces a spec
2. Dev agent reads the spec and implements
3. Test agent reads the spec and implementation and writes tests
4. Human reviews

This requires multi-step handoffs that the current conversation model doesn't easily support. Subagents (Dev Lead, Test Enforcer) exist but run synchronously and return a single message — they can't do multi-turn work like "read the spec, then implement, then report back."

**The Explore subagent failed.** When asked to explore the codebase, it requested a model (Claude Haiku 4.5) that exceeded the cost tier. This is a real constraint: not all AI capabilities are available at all times.

**The AI defaulted to doing everything itself.** Given a broad assignment, the natural tendency was to start implementing rather than coordinating. This is partly a tool constraint (subagents are limited) and partly an AI behavior pattern: "I can do this faster myself than by delegating."

### The Ugly

**The spell check debugging cycle was wasteful.** Three build-fix cycles for a naming conflict that a human developer would have caught immediately by checking the API documentation. The AI tried `GetNextSpellingErrorPosition` (doesn't exist), then `SpellCheck.GetSpellingError` (name conflict with XAML property), then finally `TextBox.GetSpellingError` (correct). Each cycle required a full build. This is the kind of thing that makes "vibe coding" look bad.

**No tests were written.** The Test Enforcer agent exists specifically to prevent this. It wasn't invoked. The features shipped without test coverage.

**The branch management was sloppy.** At one point changes were staged on `main` instead of `roadmap` because the terminal's working directory got confused. This required a `git stash` + `checkout` + `stash pop` to recover.

---

## What This Says About AI-Assisted Development

### AI is good at:
- Producing structured documents from diverse sources (competitive analysis)
- Following established code patterns (MVVM, existing service patterns)
- Generating boilerplate across many files consistently
- Self-correcting when given clear error messages

### AI is bad at:
- Multi-agent coordination without explicit orchestration
- Knowing when to delegate vs. when to do the work itself
- Writing tests proactively (needs to be forced)
- API discovery — it guesses method names instead of checking docs
- Maintaining state across many conversation turns (branch management)

### The bottleneck is coordination, not capability.
The individual tasks (write spec, implement feature, write tests) are all within AI capability. The challenge is orchestrating them in the right order with the right handoffs. A human PM would:
1. Write the spec
2. Hand it to a dev
3. The dev implements
4. The dev hands it to a tester
5. The tester writes tests
6. The PM reviews

An AI system needs the same structure. Without it, you get a single AI doing everything in one go — which works for small features but doesn't scale and doesn't produce the documentation trail the assignment requires.

---

## Recommendations for the Rest of the Assignment

1. **Use the Dev Lead agent for implementation.** Give it the spec and let it work. It has access to file system tools and can make changes across multiple files.

2. **Use the Test Enforcer agent after every feature.** Give it the spec and the changed files. It will push back if tests are missing.

3. **Keep the PM role with the coordinating AI.** The competitive analysis and feature prioritization require judgment about what matters for screen reader users. This is the hardest thing for AI to get right without human context.

4. **Document every handoff.** The story of building accessible software with AI is in the handoffs — what the PM specified, what the Dev built, what the Test caught. That's the narrative.

5. **Accept that some things will fail.** The Explore agent failed. The spell check took three tries. This is data. The honest story includes the failures.

---

## Next Steps

The coordinating AI (me) will:
1. Write a PM spec for the next feature (ICS calendar handling)
2. Hand it to the Dev Lead agent for implementation
3. Hand the result to the Test Enforcer for verification
4. Document what happens at each step

This is the experiment Kelly asked for. Let's run it properly.

---

*This document is part of the QuickMail roadmap. It will be updated as the experiment continues.*

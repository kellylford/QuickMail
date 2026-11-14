# What I Did On My Summer Vacation

When you were a kid, one of the first things you got asked on returning to school was what you did on your summer vacation. Well, now that I'm mostly retired, I guess there's no returning to school or work — but on this summer vacation I made a new email program. Here's a bit more.

It's called QuickMail. It's a Windows desktop email client, it's free, and it started on May 9 with an empty folder and the least imaginative commit message available to me: "Initial commit."

## The Short Version

- **First commit:** May 9, 2026
- **First release:** May 10, 2026 — v0.5.0, about thirty hours later
- **Latest release:** v0.8.37, August 4
- **Releases in between:** 41 of them, roughly one every other day
- **Commits:** a little over 1,100

The three-month mark falls on August 9. So this was, quite literally, a summer project.

## What It Looked Like On Day Two

The v0.5.0 release notes listed six features, and I can reproduce the whole list without it taking up much of your time:

- Multiple IMAP/SMTP accounts at once
- A unified inbox across all of them
- Conversation threading
- HTML rendering in a WebView2 pane
- Keyboard-first navigation
- Passwords in the Windows Credential Manager instead of a text file

There were eleven keyboard shortcuts, four windows, and about 4,700 lines of code. There were zero tests, which I mention now so that I can mention later that this changed.

## What It Looks Like Now

Rather more. The parts I'd point at first:

- **Two mail backends.** IMAP is still there, but Microsoft 365 accounts now go through the Graph API instead, which turns out to matter a great deal for how quickly things sync and how much of your mailbox actually works.
- **Modern sign-in.** OAuth for Google and Microsoft, so you're not typing an app password into a dialog box and hoping.
- **Calendar and contacts.** Neither existed in May. Both now sync from CalDAV, Google, and Microsoft, with an address book, contact groups, and a calendar you can actually navigate by keyboard.
- **Rules.** Both the kind that run in QuickMail and the kind that run on the server so they apply everywhere.
- **Tabs and separate windows,** if the classic reading pane isn't how your brain works.
- **A compose window that grew up:** rich text, Markdown, templates, spell check.
- **Themes, customizable message list columns, remappable keyboard shortcuts, and a command palette** for the things you can't remember the shortcut for.
- **Automatic updates,** an installer, and native ARM64 builds for the newer Windows laptops.

The file count went from 49 to 696. The code went from about 4,700 lines to about 125,400. The four windows became thirty-eight. And those zero tests became 182 test files, which is the single change I'd most want to go back and tell May Kelly about.

## The Bit I Actually Want To Talk About

Here's the thing about that list: a meaningful share of it wasn't my idea.

QuickMail has 229 issues filed against it. About 65 of those came from other people — folks who downloaded a very rough email client from a stranger on the internet, ran it against their real mail, and then took the time to write down exactly what went wrong. Some of the best features in the app started as somebody being politely annoyed at me.

A few examples, because credit should be specific:

- **Samuel Proulx** wanted to archive messages instead of deleting them, wanted a default folder to open on launch, and pointed out that the "deleting message" and "message deleted" announcements were talking over the next message in the list. All three were right. All three got fixed.
- **Nolan Roberts** asked for notification support, which became a whole feature with toasts, close-to-tray, and the ability to click a notification and land on the actual message.
- **Christopher Wright** asked for contact syncing from the server and the ability to mail people you'd written to before.
- **Brian Vogel** reported that the interface was "riddled with oddly placed underscores." It was. That one produced a rule in the project's own coding standards so it wouldn't happen again.
- **Chris Stoneman, Dennis L, Bruno Prieto, Taylor Arndt, and others** filed reports on everything from iCloud's Sent folder to whether there might someday be a Mac version. (No promises.)

And then there's the code. **Timothy Spaulding** started contributing on May 31 and hasn't really stopped: 96 commits and 51 merged pull requests, on genuinely hard things — mail sync performance, the Microsoft Graph message-ID problem that made deleted mail reappear like a bad houseguest, server-side rules. Large parts of the app work properly because of him. **André Polykanine** built the first real installer back in June, back when "installing QuickMail" meant putting an .exe somewhere and remembering where. **Brandon** pitched in as well.

I've written before on this blog about accessibility being treated as somebody else's job. It's a genuine pleasure to report the opposite experience: a bunch of people showed up, unprompted, and made a thing better for everyone who uses it. Thank you. Sincerely.

## Why Build This At All

Because I wanted an email client that was designed for the keyboard from the first line of code rather than having keyboard access retrofitted onto it later, and because I use a screen reader all day and I'm tired of the retrofit.

That's the whole design brief. Every list is navigable. Every action has a shortcut, and you can change any of them. Announcements are categorized so you can turn off the ones you find chatty without losing the ones you need. When the app tells you something, it's because it's something Windows wouldn't have told you already.

## About The Robot In The Room

I built this with AI assistance — Claude, mostly, working in the codebase alongside me. Regular readers won't be shocked; I've written about AI-assisted development here before. The commit history is public and honest about it.

What I'll say is this: the AI is very good at the tenth implementation of a dialog box and completely useless at knowing whether a screen reader announcement is helpful or maddening. That part is still a human job, and specifically a job for the human who has to listen to it. The design decisions, the accessibility calls, and the "no, that's wrong, here's what I actually hear" corrections were all mine. It was a genuinely productive way to work, and it is not a substitute for knowing what you want.

## Try It

QuickMail is free and open source under the MIT license. It runs on Windows 10 and 11, on both Intel and ARM machines.

- **Download:** [github.com/kellylford/QuickMail/releases/latest](https://github.com/kellylford/QuickMail/releases/latest)
- **User guide:** [kellylford.github.io/QuickMail](https://kellylford.github.io/QuickMail/)
- **Bugs and ideas:** [github.com/kellylford/QuickMail/issues](https://github.com/kellylford/QuickMail/issues) — as the list above shows, I read them and act on them.

One last thing. I'm writing this on August 6, based on version 0.8.37. Given the pace of the last three months, there's a decent chance a couple of features have landed since I typed this sentence. So check the [latest release](https://github.com/kellylford/QuickMail/releases/latest) — or, if you already have QuickMail installed, open the **Help** menu, choose **Check for Updates**, and find out for yourself.

Summer's not quite over yet.

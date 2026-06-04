## Summary

<!-- What does this PR do and why? Link to the relevant issue if one exists. -->

Closes #

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Accessibility fix or improvement
- [ ] Refactor / cleanup
- [ ] Documentation
- [ ] Build / tooling

## Checklist

- [ ] All tests pass (`dotnet test QuickMail.Tests/QuickMail.Tests.csproj -c Release`)
- [ ] New or changed behaviour has test coverage
- [ ] CLAUDE.md conventions followed (MVVM rules, modal dialog rules, keyboard shortcut registration)
- [ ] Keyboard shortcuts registered in `CommandRegistry` — no raw key checks for new actions
- [ ] Custom announcements use `AccessibilityHelper.Announce()` with a `category` argument
- [ ] No instructional text in `AutomationProperties.HelpText` or `AutomationProperties.Name`
- [ ] User-facing text updated in `USERGUIDE.md` if applicable
- [ ] Release notes updated in `docs/release-notes-v*.md` if applicable

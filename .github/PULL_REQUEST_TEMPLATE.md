## What and why

<!-- What this changes and the problem it solves. Keep the pull request small and focused. -->

## How it was checked

<!-- The commands you ran and what you verified by hand. CI runs the same jobs (CONTRIBUTING.md §11). -->

- [ ] Backend: `dotnet build src/Cinomni.slnx` and the affected test projects
- [ ] Web: `npm run typecheck && npm run lint && npm run test && npm run build`
- [ ] A field the web client reads is asserted through an HTTP request

## Before merging

- [ ] Conventional commit messages, in English
- [ ] No secrets, private paths or tracker details in code, tests or logs
- [ ] A new or upgraded dependency records its licence and provenance
- [ ] A migration is safe on an existing installation, or declares why it is not
- [ ] CHANGELOG.md `Unreleased` updated for anything an operator would notice

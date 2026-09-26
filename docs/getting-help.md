# Getting help

## Before you ask

- Check [Troubleshooting](administration/troubleshooting.md) and the [FAQ](faq.md).
- Read the release notes of your version in [CHANGELOG.md](../CHANGELOG.md): an alpha has known gaps,
  and they are listed there.
- Look at **Console → System** and at the reason Cinomni recorded (on **Activity**, on the title's
  page, or under **Console → Operations**). Most failures carry one.

## Reporting a bug

Open an issue on [GitHub](https://github.com/Cinomni/cinomni/issues/new/choose) using the bug report
form. Include:

- the **version**, from **Console → System → Build** or the page footer;
- **how it runs**: the compose files and overlays you use, the host OS, and the GPU if it is about
  transcoding;
- what you did, what you expected, and what happened instead;
- the relevant **log lines** (`docker compose logs cinomni`).

Before pasting logs, remove anything private: paths and titles in your library, your addresses, and
above all any secret. A backup's `.manifest.json` is safe to share; a `.dump` file and `.env` are not.

## Suggesting a feature

Use the feature request form. Check [ROADMAP.md](../ROADMAP.md) first: it lists what is planned and
what is deliberately out of scope.

## Reporting a security vulnerability

**Never in a public issue.** Report it privately through a
[GitHub security advisory](https://github.com/Cinomni/cinomni/security/advisories/new). See
[SECURITY.md](../SECURITY.md).

## Contributing

Improvements to the code and to this documentation are welcome. Start with
[CONTRIBUTING.md](../CONTRIBUTING.md).

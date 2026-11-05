# Code signing policy

Free code signing for XM Panel is provided by [SignPath.io](https://signpath.io), with the certificate issued by [SignPath Foundation](https://signpath.org).

Releases are only signed when they are built automatically by [GitHub Actions](.github/workflows/build.yml) from the source code in this repository. Every signing request is approved manually by a project approver.

## Team roles

XM Panel is maintained by one person, who holds all roles:

- **Authors** (may change the source code without further review): [Holse18](https://github.com/Holse18)
- **Reviewers** (review all contributions from others, e.g. pull requests): [Holse18](https://github.com/Holse18)
- **Approvers** (approve each signing request): [Holse18](https://github.com/Holse18)

All team members use multi-factor authentication for GitHub and SignPath.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

XM Panel talks only to your own headphones over Bluetooth. It doesn't use the internet, collect analytics or send any data anywhere. Settings and logs are stored locally in `%LOCALAPPDATA%\XMPanel`.

## Changes to your system

- **Start with Windows** (a switch in Settings, off unless you turn it on) adds or removes an entry in your user's Windows startup list (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).
- **Tray icon:** the first time it runs, XM Panel sets its tray icon to show on the taskbar instead of in the overflow menu (a per-user Windows setting). You can change this in Windows Settings → Personalization → Taskbar → Other system tray icons.
- **Start Menu shortcut:** only `build.cmd` (building it yourself) creates one.

See [Uninstall](README.md#uninstall) for how to remove everything.

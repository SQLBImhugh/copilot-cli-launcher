# Copilot Launcher — Legacy PowerShell kit

> **Status: maintenance mode.** This is the original PowerShell-based launcher (versions 1.x). New development happens in [Copilot Launcher 2.0](../README.md), a WinUI 3 desktop app with a session picker, saved-launches manager, and centralized settings.

If you're already happily using the PS launcher, **you can keep using it** — nothing here is being deleted, and the bug-fix patches (DateTime in changelog briefings, copilot-process resolution, Win32 keep-alive workaround) are already in place. New features go to 2.0; this folder gets security/correctness fixes only.

## Why the rewrite?

The PowerShell kit grew organically and hit some limits:

- **Scattered shortcuts**: each project needed its own `.lnk` file on the Desktop, with its own arg list. Hard to manage.
- **No session awareness**: the launcher could only `--resume` a single hard-coded session name per shortcut. There was no way to browse the ~150 sessions accumulated in `~/.copilot/session-state/` and pick one.
- **Re-running the installer to change one setting** was clunky. The 2.0 app keeps everything live in a Settings tab.
- **Configurable terminal**: 2.0 lets you pick *any* terminal (Windows Terminal, pwsh, ConEmu, Hyper, Tabby, etc.) via a token-based arg template.

## What's still here

```
legacy/
├── Launch-Copilot.ps1            ← the launcher (still runs fine)
├── New-CopilotShortcut.ps1       ← post-install shortcut helper
├── installer-template.ps1        ← installer with {{X_B64}} placeholders
├── build-installer.ps1           ← bundles source files into legacy/dist/
├── repair-copilot-sessions.py    ← session events.jsonl repair helper
├── config.example.json
├── agents.example.md
└── dist/
    ├── Install-CopilotLauncher.ps1   ← bundled distributable
    └── install.ps1                   ← BOM-free bootstrap for `iwr | iex`
```

## Install (legacy)

```powershell
iwr -useb https://github.com/SQLBImhugh/copilot-cli-launcher/raw/main/legacy/dist/install.ps1 | iex
```

> **URL updated**: the bootstrap moved from `dist/install.ps1` to `legacy/dist/install.ps1` when the kit was reorganized. The old URL no longer exists.

For full setup options (silent install, project name, AI summary, etc.), see the [original README](../README.md) — wait, that points at 2.0 now. Browse the historical docs by viewing this commit before the rewrite:

```powershell
git log --diff-filter=R --follow --pretty=format:"%H %s" legacy/Launch-Copilot.ps1 | Select-Object -First 1
```

Or just walk the `Launch-Copilot.ps1` and `installer-template.ps1` files directly — they're heavily commented.

## Migrating to 2.0

The 2.0 app's first-run wizard auto-detects an existing legacy install at `%USERPROFILE%\copilot-launcher\` and offers to import:

- Your `config.json` → 2.0's `settings.json`
- Your `agents.md` (untouched, copied to the 2.0 app data folder)
- Detected `.lnk` shortcuts on Desktop → 2.0 saved launches

You can choose to skip the import and start fresh; the wizard never auto-merges. The legacy install on disk is **not** modified or removed by 2.0 — you keep both side-by-side as long as you want.

## Future of this folder

- ✅ Bug fixes for correctness or security
- ✅ Patches that mirror upstream Copilot CLI changes (e.g., new `copilot update` output formats)
- ❌ New features
- ❌ UX changes

If 2.0 reaches feature parity *and* you've migrated, you can `git rm -r legacy/` to clean up your local clone. The repo will keep the folder so existing one-liner URLs in the wild continue to resolve.

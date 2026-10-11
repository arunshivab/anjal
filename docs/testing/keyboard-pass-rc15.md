# Keyboard-only pass (UX-09, rc.15)

The test record the owner asked for on 7 October 2026. Made by the screen checks (tools/ui, check "keyboard"), which also run on every change as the "screens" GitHub check; the sample data is tools/seed_sample_mail.py, signed in as an operator who is the organisation's administrator.

Checked 2026-10-07 at 1366x768 by tools/ui (check "keyboard"): on each kind of screen, Tab pressed from the top until focus came round again. Every stop must be visible and show that it has focus; focus must never be trapped; every control showing must be reached.

| Screen | Tab stops | Controls showing | Problems |
|---|---:|---:|---:|
| `/folder/INBOX` | 319 | 319 | 0 |
| `/folder/Sent` | 31 | 31 | 0 |
| `/folder/Drafts` | 31 | 31 | 0 |
| `/folder/Junk` | 40 | 40 | 0 |
| `/folder/Trash` | 32 | 32 | 0 |
| `/folder/Archive` | 31 | 31 | 0 |
| `/folder/Scheduled` | 31 | 31 | 0 |
| `/outbox` | 21 | 21 | 0 |
| `/no-reply` | 22 | 22 | 0 |
| `/compose` | 59 | 58 | 0 |
| `/search?q=the` | 74 | 74 | 0 |
| `/contacts` | 29 | 29 | 0 |
| `/dashboard` | 83 | 83 | 0 |
| `/dashboard/org` | 52 | 52 | 0 |
| `/settings` | 34 | 51 | 0 |
| `/org` | 38 | 38 | 0 |
| `/ops` | 57 | 57 | 0 |
| `/settings/security` | 50 | 50 | 0 |
| `/org/domains?domain=qa.test` | 41 | 41 | 0 |
| `/org/apps` | 31 | 31 | 0 |
| `/org/log?kind=sign-in&days=1` | 48 | 48 | 0 |
| `/org/people` | 38 | 38 | 0 |
| `/settings/appearance` | 34 | 51 | 0 |
| `/settings/language` | 33 | 41 | 0 |
| `/settings/mail` | 61 | 60 | 0 |
| `/settings/rules` | 31 | 31 | 0 |
| `/settings/categories` | 30 | 30 | 0 |
| `/settings/senders` | 30 | 30 | 0 |
| `/settings/help` | 31 | 31 | 0 |
| `/org/signin` | 40 | 40 | 0 |
| `/org/shared` | 31 | 31 | 0 |
| `/org/retention` | 38 | 38 | 0 |
| `/org/sending` | 32 | 32 | 0 |
| `/org/branding` | 36 | 48 | 0 |
| `/ops/overview` | 57 | 57 | 0 |
| `/ops/orgs` | 32 | 32 | 0 |
| `/ops/limits` | 26 | 25 | 0 |
| `/ops/health` | 26 | 26 | 0 |
| `/message/{id}/print` | 0 | 0 | 0 |
| `/message/{id}/original` | 23 | 23 | 0 |
| `/settings/security/two-step` | 37 | 37 | 0 |
| `/message/{id}` | 36 | 37 | 0 |

Screens: 42. Problems: 0.

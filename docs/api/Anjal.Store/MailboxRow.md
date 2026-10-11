# MailboxRow

**Namespace:** `Anjal.Store`

A mailbox: a receiving identity local_part@domain belonging to a tenant. The mailbox also carries submission credentials so that the same identity can authenticate on the submission port and send as itself (Dovecot/Postfix "virtual user" pattern).

## Members

- **DefaultQuotaBytes** *(field)* - One gibibyte - the default per-mailbox quota (owner, 10 Oct 2026, DES-11 D2).
- **DefaultTheme** *(field)* - Default webmail theme: Anjal, the house colour of the approved boards, light (rc.15).
- **NormalizeTheme** *(method)* - The theme to use for a stored or remembered value: a known theme as it is; a theme name from before rc.11 converted (ink and midnight were dark, paper and postcard light); anything else the default.
- **Address** *(property)* - The full address local_part@domain.
- **CreatedAt** *(property)* - When the mailbox was created.
- **DateFormat** *(property)* - Date format (rc.11): one of .
- **Density** *(property)* - Comfortable or compact (rc.11).
- **DisplayName** *(property)* - Display name for the From header (e.g. "Arun Shiva B").
- **Domain** *(property)* - Domain of the address, lowercase. Must be a domain of the tenant.
- **Enabled** *(property)* - When false, delivery and authentication both fail.
- **Id** *(property)* - Identifier assigned by the store.
- **Language** *(property)* - Webmail language (rc.11): one of .
- **Layout** *(property)* - Three panes, focus, or list only (rc.11).
- **LocalPart** *(property)* - Local-part of the address, lowercase (left of "@", no "+tag").
- **NewMailSound** *(property)* - Play a sound when new mail arrives (rc.11); on by default.
- **PageSize** *(property)* - Messages per page (rc.11): one of .
- **PasswordPbkdf2** *(property)* - PBKDF2 password hash in Anjal.Smtp.Pbkdf2Hasher format, used for submission authentication. Empty means the mailbox is receive-only and cannot authenticate.
- **QuotaBytes** *(property)* - The mailbox's own size limit in bytes, enforced (DES-11 D2): a full mailbox receives nothing (452) and cannot send. 0 when it has none of its own, under a shared storage plan (see ).
- **RailChosen** *(property)* - True once the person has folded or opened the rail themselves (rc.15); until then it folds by itself on laptop screens.
- **RailFolded** *(property)* - True when the rail shows icons only (rc.11).
- **TenantId** *(property)* - The owning tenant.
- **Theme** *(property)* - Webmail theme for this mailbox: a colour and a mode, such as teal-light, teal-dark or teal-auto. Stored per mailbox, not per browser.
- **ThemeColours** *(property)* - The house colour, Anjal, then the twenty named colours, in the order Settings shows them.
- **ThemeModes** *(property)* - The theme modes: light, dark, or following the device's own setting.
- **TimeZone** *(property)* - IANA time zone every date is shown in (rc.11). Saved through .
- **UpdatedAt** *(property)* - When the mailbox was created or last updated.
- **UsedBytes** *(property)* - Bytes currently stored in this mailbox, maintained by the store on each delivery.
- **WeekStart** *(property)* - First day of the week (rc.11): monday or sunday.
- **WelcomeDone** *(property)* - The welcome screen has been answered (rc.11, D-105).

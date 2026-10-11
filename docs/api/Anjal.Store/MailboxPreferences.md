# MailboxPreferences

**Namespace:** `Anjal.Store`

A person's own settings for how the webmail looks and tells time (rc.11): time zone, language, density, layout, the folded rail, date format, the first day of the week, messages per page and the new-mail sound. They are saved on their own, never by a general mailbox save, so no other change can reset them.

## Members

- **DefaultDateFormat** *(field)* - The date format a new mailbox starts with: as its language writes dates.
- **DefaultDensity** *(field)* - The density a new mailbox starts with.
- **DefaultLanguage** *(field)* - The language a new mailbox starts with.
- **DefaultLayout** *(field)* - The layout a new mailbox starts with: three panes.
- **DefaultPageSize** *(field)* - Messages per page for a new mailbox: 50, as before rc.11.
- **DefaultTimeZone** *(field)* - The time zone a new mailbox starts with.
- **DefaultWeekStart** *(field)* - The first day of the week a new mailbox starts with.
- **IsKnownTimeZone** *(method)* - True when names a time zone this server knows.
- **Normalized** *(method)* - A copy in which every value is one that exists: known values kept (lower-cased, trimmed), anything else replaced by its default.
- **Of** *(method)* - The preferences stored on a mailbox.
- **DateFormat** *(property)* - One of .
- **DateFormats** *(property)* - As the language writes dates, or one fixed form.
- **Densities** *(property)* - Comfortable or compact.
- **Density** *(property)* - One of .
- **Language** *(property)* - One of .
- **Languages** *(property)* - The six languages, English first: Tamil, Malayalam, Hindi, Marathi, Gujarati.
- **Layout** *(property)* - One of .
- **Layouts** *(property)* - Three panes, focus (the letter and its envelope), or list only.
- **NewMailSound** *(property)* - True to play a sound when new mail arrives; on by default (D-99).
- **PageSize** *(property)* - One of .
- **PageSizes** *(property)* - The page sizes a person may choose (owner, item 14).
- **RailChosen** *(property)* - True once the person has folded or opened the rail themselves; until then the rail shows icons only on screens up to laptop width.
- **RailFolded** *(property)* - True when the rail shows icons only.
- **TimeZone** *(property)* - IANA time zone, such as Asia/Kolkata. Every date and time is shown in it.
- **WeekStart** *(property)* - One of .
- **WeekStarts** *(property)* - Monday or Sunday.
- **WelcomeDone** *(property)* - True once the welcome screen has been answered (D-105): it is never shown again.

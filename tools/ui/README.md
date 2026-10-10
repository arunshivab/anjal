# Screen checks

Opens every webmail screen in a real browser (Chromium, through Playwright) and checks what a
person would notice. Added in rc.15 for item 47 and UX-09, together with the system-wide checks
the owner asked for on 7 October 2026. The "screens" GitHub check runs them on every change.

| Check | What it looks for |
|---|---|
| `noscroll` | Mail screens (folders, a message, search, compose, Outbox, No reply) never scroll as a whole page at 1366×768, with and without a mail ticked (item 47). Dashboards, the consoles, Contacts and Settings may scroll their middle area. |
| `names` | Every button, link, box, choice and picture has a name a screen reader can say (axe-core), with menus closed and open (UX-09). |
| `tips` | Every text button outside menus and windows has a tip saying what it does (item 5, UX-09). |
| `menus` | Every menu opens fully on screen and on top of everything around it, at 1366×768 and 1920×1080, with and without a mail ticked. |
| `close` | Every kind of menu closes on a click outside it and on Escape. |
| `align` | Icons and words centred in their buttons; buttons side by side level; nothing moves when the pointer rests on it or when its menu opens. |
| `shrink` | Nothing inside a scrolling column is squeezed smaller than its content. |
| `clip` | No words cut off or spilling out of a choice box, a button or a text box's example text, and nothing reaching past its table cell, with every menu open too, at three sizes (on a laptop screen also with the folder names kept open). |
| `same` | Each kind of control and text (text boxes, choice boxes, buttons of each size, headings, labels, notes, tables, pills) has one look on every screen; any other look is listed with where it was seen (owner, 8 Oct 2026). Measured at 1536×730. |
| `steady` | The frame stays exactly in place: the rail's icons (folder names showing and folded) and the status bar, on every screen, whatever is open beside them (owner, 8 Oct 2026). |
| `lineup` | In every mail list every subject starts at the same place, whether or not the mail shows the lock, with nothing open, with a message open and on a phone (owner, 25 Sep and 9 Oct 2026). |
| `sizes` | Three heights in the whole app (owner, 10 Oct 2026): regular 36px for buttons, text boxes and choice boxes; small 28px for actions inside a row, a toolbar or a strip of choices; large 48px only on the sign-in pages and for code boxes. Controls side by side on one row are one height; a strip of choices counts as its box. Measured at 1366x768 and 1920x1080 (the phone keeps its touch sizes). |
| `scrollbars` | Nothing is drawn over a scroll bar (owner, 10 Oct 2026): every box that scrolls, on every screen at 1366x768, 1536x730 and 1920x1080, and any mark, badge, button or panel outside it that covers its scroll bar. Every check draws real scroll bars, as Windows does; a headless browser hides them unless asked. |
| `flap` | With three panes side by side, the marigold flap sits beside the open card, before the list's scroll bar (owner, 10 Oct 2026), in the gap beside the open card, at its middle, over neither the card nor the letter; it follows the card as the list scrolls and goes when the card is out of view; none on a phone (owner, 10 Oct 2026, DES-11 D9). |
| `swap` | No full page reloads (owner, 9 Oct 2026): every rail link, Back and Forward, a search and a choice that applies at once swap the page in place - no page load, the address and title change, focus goes to the new heading, no script error - and each page swapped in matches a full load of the same address. |
| `keyboard` | Keyboard only: on each kind of screen, Tab from the top until focus comes round; every stop visible and showing focus, focus never trapped, every control reached. Writes `out/keyboard.md`, the test record (UX-09). |
| `reach` | Every link opens and every button does its job: no error page, no refused request, no script error. **It presses Delete too.** |

The screens are found by following links from the main ones, signed in as someone who is an
operator and an organisation's administrator, so the organisation and Anjal consoles are included
(at most two addresses of each kind, twelve on one path). The list is written to `out/screens.txt`.

## Running it

Only ever against a throwaway copy: `reach` presses every button.

On Linux (as GitHub does), with PostgreSQL running and a Release build:

```
PGPASSWORD=<postgres password> bash tools/ui/setup.sh    # new database anjal_ui, sample mail, webmail on 5080
cd tools/ui
npm install
npx playwright install chromium
node check.js                       # every check; exit code 1 when anything is found
node check.js --only names,tips     # some checks
node check.js --skip reach          # all but pressing every button
node check.js --only clip --lang ta # a language preview: findings listed for the language check, not failed
bash setup.sh stop
```

Settings: `ANJAL_UI_BASE` (default `http://127.0.0.1:5080`), `ANJAL_UI_ADDRESS` and
`ANJAL_UI_PASSWORD` (the sample mailbox, `arun@qa.test`), `ANJAL_UI_CHROMIUM` (a Chromium to use
instead of Playwright's own), `ANJAL_UI_OUT` (reports; default `tools/ui/out`).

Each check's findings go to `out/<check>.txt`; on GitHub they are kept with the run as
"screen-checks".

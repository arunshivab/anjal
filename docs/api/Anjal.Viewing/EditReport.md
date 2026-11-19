# EditReport

**Namespace:** `Anjal.Viewing`

What Save did (§7.6; logged as counts, never content).

## Members

- **ActiveRemoved** *(property)* - Scripts, actions and the like the sanitizer removed.
- **FilesRemoved** *(property)* - Embedded files the sanitizer removed.
- **NothingUnder** *(property)* - The pages (0-based) that have a redaction box under which nothing was found: a note, not a refusal.
- **OutlineRemoved** *(property)* - The outline was removed (a redacted file keeps none, D-375).
- **Pages** *(property)* - The pages of the result.
- **Redacted** *(property)* - The boxes redacted.
- **Refused** *(property)* - When the save is refused: why ().
- **Removed** *(property)* - What the redaction removed, page by page (chuvadi-pdf 3.34.0 Redactor.ApplyWithReport), shown to the person before the file is attached (decisions of 15 Nov 2026). Pages where nothing was removed are not listed.
- **SurvivorBox** *(property)* - When the save is refused: the box something still shows in.
- **SurvivorPage** *(property)* - When the save is refused: the page (0-based) where something still shows inside a box.

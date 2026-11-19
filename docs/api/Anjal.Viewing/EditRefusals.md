# EditRefusals

**Namespace:** `Anjal.Viewing`

Why a save is refused (DES-22 §7.6, §7.8).

## Members

- **DigitallySigned** *(field)* - The file is digitally signed and the person did not accept that the signature will no longer hold.
- **Layers** *(field)* - The plan changes the pages of a file with hidden layers, which waits for chuvadi-pdf 3.35.1 (D-406).
- **Leftovers** *(field)* - The final bytes still hold an outline, document information, XMP or embedded files (§7.8, 7).
- **Password** *(field)* - The file has a password and the person did not accept that the result will not.
- **Plan** *(field)* - The plan does not fit the file (a page or a picture that is not there).
- **RedactOff** *(field)* - The plan redacts, and redaction is off until chuvadi-pdf 3.35.1 (D-405).
- **Reopen** *(field)* - The result did not open again, or its first page could not be drawn (§7.8, 4).
- **Skipped** *(field)* - The compressor skipped the file, so its metadata could not be removed (D-375, step 6).
- **Survivor** *(field)* - Something still shows inside a redaction box (§7.6, the gate).

# PaperForm

**Namespace:** `Anjal.Store`

rc.25 (DES-15 §6.2): how secrets are written on paper - hexadecimal (not base64; the lesson of 1 October), in groups of four, with a check group of four made from the SHA-256 of the value, so a mistyped character is found at once.

## Members

- **Read** *(method)* - Read what was typed from paper; null when it does not check.
- **Write** *(method)* - Write bytes for paper: "ABCD EF01 … CHK1".

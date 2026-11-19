# EditPlan

**Namespace:** `Anjal.Viewing`

rc.29 part 2 (DES-22 §7, D-369): every change the person made in the PDF editor, applied in one pass on Save. Positions are in points in the page as shown (top-left origin, after the page's turn and crop), on the pages as they will be (), so what the person drew is where it lands.

## Members

- **ChangesPages** *(method)* - Whether the plan changes the pages (D-406): joins another file, or lists pages other than the file's own, each in its place and with no turn.
- **Whole** *(method)* - The plan with every list there (empty when the JSON left it out): the JSON reader gives an init-only list it did not find as null, so a plan from the page is read through this before anything uses it.
- **AcceptNoPassword** *(property)* - The person ticked that the result will have no password (D-374).
- **AcceptSignedRewrite** *(property)* - The person ticked "I understand" for a digitally signed file (§7.8, 1).
- **IsEmpty** *(property)* - Whether the plan changes nothing at all.
- **Joined** *(property)* - How many of the request's files after the first are the mail's other PDFs to join (D-371).
- **JoinedPasswords** *(property)* - The passwords of the joined PDFs, in their order (null for one without).
- **Marks** *(property)* - Marks, signatures and pictures, drawn in list order (§7.4, §7.5).
- **Numbers** *(property)* - Page numbers, a header or a footer on every page (§7.7), or none.
- **Pages** *(property)* - The pages in their new order, each with its turn; empty keeps the file's pages as they are.
- **Redactions** *(property)* - The boxes to redact (§7.6).
- **Watermark** *(property)* - A watermark on every page (§7.7), or none.

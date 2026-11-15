# PdfJobs

**Namespace:** `Anjal.Viewer`

rc.29 (DES-22 §3.1, D-359): a PDF drawn by Anjal's viewer instead of the browser's own - its pages as pictures, its text laid over them, Find, the outline, a password asked for, and the notices. Every call opens the file with the untrusted limits of DES-22 §5 and a cancellation token.

## Members

- **MaxHits** *(field)* - At most this many matches are given back from one search.
- **MaxWidth** *(field)* - The widest page picture drawn (DES-22 §3.1).
- **MinWidth** *(field)* - The narrowest page picture drawn (a thumbnail is 120 px).
- **ActiveContent** *(method)* - Whether the PDF holds scripts, actions or embedded files (DES-22 §8).
- **Clean** *(method)* - A copy without scripts, actions, embedded files, XFA and multimedia (D-367: Download without them).
- **Find** *(method)* - Finds words; each match's boxes are placed in its page as shown.
- **Info** *(method)* - A PDF's pages as shown, its outline and its notices.
- **Page** *(method)* - One page drawn as a PNG at a width in pixels (60 to 2,000).
- **Text** *(method)* - One page's text runs, placed in the page as shown.
- **Limits** *(property)* - DES-22 §5: the limits every PDF is opened with.

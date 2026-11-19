# LipiText

**Namespace:** `Anjal.Viewer`

rc.29 part 2 (DES-22 §7.4, §7.7): how wide words are in LiPi Sans, the face the builder draws "Lipi" text with, measured by shaping each script's run with that script's LiPi face - so a text box wraps, and a watermark or a page number is centred, by the words' real width in every script (the Standard 14 metrics know only Latin).

## Members

- **Width** *(method)* - The width of one line of text, in points.
- **Wrap** *(method)* - The lines a text fills in a box of a width: the person's own line breaks kept, long lines wrapped at spaces.

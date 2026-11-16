# WordRun

**Namespace:** `Anjal.Viewing`

AC-24: one stretch of a Word paragraph with one formatting, as the library resolves it (document defaults, styles, then the run's own). A line break is a run whose text is a single new line.

## Members

- **IsPlain** *(method)* - True when the run has no formatting to show.
- **SameLook** *(method)* - True when the other run has the same formatting.
- **B** *(property)* - Bold.
- **C** *(property)* - "caps" all capitals, "small" small capitals, or null.
- **I** *(property)* - Italic.
- **P** *(property)* - "sup" raised, "sub" lowered, or null.
- **S** *(property)* - Struck through (single or double).
- **T** *(property)* - The text.
- **U** *(property)* - Underlined (any underline style).

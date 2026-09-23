# SpamScorer

**Namespace:** `Anjal.Spam`

Minimum-heuristics spam scorer. Adds points for each signal that fires and returns the total with the reasons; the caller compares the score to the tenant's threshold. Authentication results are read from when present. DNS questions go through and are skipped when no lookup is configured.

## Members

- **#ctor** *(method)* - Construct.
- **DomainOf** *(method)* - Lowercase domain of an address, or empty.
- **FirstAddress** *(method)* - The first address in a header value, lowercased, or empty.
- **IsAllCaps** *(method)* - Whether a subject is shouting in capitals. Only CASED letters count: Tamil, Devanagari, Malayalam, Arabic, Hebrew, Thai, Chinese, Japanese and Korean have no capitals at all, and counting their letters as "not lowercase" charged a spam point to every ordinary subject in the languages this product is built for (DEF-039).
- **ScoreAsync** *(method)* - Score a delivery. Never throws: a rule that cannot be evaluated is skipped.
- **Options** *(property)* - The options in effect.

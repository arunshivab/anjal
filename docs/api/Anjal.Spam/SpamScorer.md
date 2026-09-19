# SpamScorer

**Namespace:** `Anjal.Spam`

Minimum-heuristics spam scorer. Adds points for each signal that fires and returns the total with the reasons; the caller compares the score to the tenant's threshold. Authentication results are read from when present. DNS questions go through and are skipped when no lookup is configured.

## Members

- **#ctor** *(method)* - Construct.
- **DomainOf** *(method)* - Lowercase domain of an address, or empty.
- **FirstAddress** *(method)* - The first address in a header value, lowercased, or empty.
- **ScoreAsync** *(method)* - Score a delivery. Never throws: a rule that cannot be evaluated is skipped.
- **Options** *(property)* - The options in effect.

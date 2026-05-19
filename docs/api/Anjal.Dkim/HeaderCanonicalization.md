# HeaderCanonicalization

**Namespace:** `Anjal.Dkim`

RFC 6376 header canonicalization algorithm.

## Members

- **Relaxed** *(field)* - Header names are lowercased, runs of internal whitespace collapsed to a single SP, trailing whitespace stripped, folded continuations unfolded. The default - tolerant of normal mail transit modifications.
- **Simple** *(field)* - Headers are reproduced verbatim. Fragile - any mailer that re-folds a long header will break the signature.

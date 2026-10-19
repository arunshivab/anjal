# SenderRuleAction

**Namespace:** `Anjal.Store`

What a does to matching mail.

## Members

- **Allow** *(field)* - Always deliver to INBOX regardless of score.
- **Block** *(field)* - Always deliver to Junk regardless of score.
- **Discard** *(field)* - Discard silently (rc.22, item 32, owner 18 Oct 2026, option (b)): the mail never appears in any folder the person sees and the sender is not told. The as-received evidence copy is still kept under the evidence rule.

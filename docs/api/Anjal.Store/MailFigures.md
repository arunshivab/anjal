# MailFigures

**Namespace:** `Anjal.Store`

What a dashboard counts for one period (rc.15, items 56 to 61): totals only - never which messages, never who. A scope is one mailbox, one organisation, or the whole service.

## Members

- **TopScore** *(field)* - The highest spam score with its own bar; higher scores share the last ("10+").
- **Checked** *(property)* - Messages that went through the incoming checks and have a score.
- **Junk** *(property)* - Messages filed in Junk.
- **Received** *(property)* - Messages received and kept out of Junk.
- **Scores** *(property)* - Checked messages by spam score, 0 to (the last is that score and above).
- **Sent** *(property)* - Messages sent (copies in Sent).
- **Series** *(property)* - Received (Junk left out, as in ) and sent, step by step through the period: each step's start in the asked zone's local time. Steps with nothing are left out.

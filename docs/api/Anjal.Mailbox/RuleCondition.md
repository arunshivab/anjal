# RuleCondition

**Namespace:** `Anjal.Mailbox`

One condition of a (rc.12, items 24 and 31).

## Members

- **Field** *(property)* - What is tested: from, to, subject, body, attachment, unsubscribe, outside or group.
- **Op** *(property)* - How: contains, is, startswith, notcontains. Ignored by attachment, unsubscribe and outside.
- **Value** *(property)* - The value compared, without regard to case; for group, the group's name.

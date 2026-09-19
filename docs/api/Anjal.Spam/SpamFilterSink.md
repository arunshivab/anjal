# SpamFilterSink

**Namespace:** `Anjal.Spam`

decorator that scores every unauthenticated delivery, records the verdict in headers, and hands the annotated message to the inner sink. Authenticated (submission) mail passes through untouched. In mode a message at or above is refused with 550 instead of being passed on.

## Members

- **#ctor** *(method)* - Construct.
- **DeliverAsync** *(method)* - _(no description)_
- **Action** *(property)* - What to do with spam. Default .
- **RejectThreshold** *(property)* - Score at or above which refuses the message. Default 5.

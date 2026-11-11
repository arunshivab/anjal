# ResealResult

**Namespace:** `Anjal.Mailbox`

What a re-seal run found and did (rc.28, D-264).

## Members

- **#ctor** *(method)* - What a re-seal run found and did (rc.28, D-264).
- **Describe** *(method)* - One line for the operator.
- **AlreadySealed** *(property)* - Originals sealed already (since rc.25, or by an earlier run).
- **Checked** *(property)* - Originals looked at (not purged).
- **DryRun** *(property)* - True when nothing was changed.
- **Individuals** *(property)* - Originals of individuals, left as they are.
- **Problems** *(property)* - Originals without a file, or not matching their record: left for the evidence check.
- **Queued** *(property)* - Queued messages sealed now (or that would be).
- **Sealed** *(property)* - Originals of organisations sealed now (or that would be, in a dry run).

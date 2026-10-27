# BatchItemResult

**Namespace:** `Anjal.Client`

One message's result in a batch (DES-14 §21).

## Members

- **Error** *(property)* - Why it was refused, when it was: {code, message, ...}.
- **ErrorCode** *(property)* - The refusal's code, or null.
- **Id** *(property)* - The send's id, when it was accepted.
- **Index** *(property)* - Its place in the batch.
- **Status** *(property)* - Its status, when accepted.

# LocalDomainRow

**Namespace:** `Anjal.Store`

A domain considered local by Anjal's MTA listener. RCPT TO addresses whose domain is not in this list are refused with 550 5.7.1 Relaying denied (open-relay guard).

## Members

- **CreatedAt** *(property)* - When the row was created.
- **Domain** *(property)* - The domain name (lowercase recommended).
- **Id** *(property)* - Identifier assigned by the store.

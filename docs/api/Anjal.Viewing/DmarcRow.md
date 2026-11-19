# DmarcRow

**Namespace:** `Anjal.Viewing`

One row of a DMARC aggregate report: one sending address's mail for the domain (rc.15, item 32).

## Members

- **#ctor** *(method)* - One row of a DMARC aggregate report: one sending address's mail for the domain (rc.15, item 32).
- **Count** *(property)* - How many messages.
- **Disposition** *(property)* - What the receiver did: none, quarantine or reject.
- **Dkim** *(property)* - DKIM, as DMARC judged it: pass or fail.
- **HeaderFrom** *(property)* - The domain in the From line.
- **Passed** *(property)* - True when DMARC passed: DKIM or SPF passed and was aligned.
- **SourceIp** *(property)* - The address that sent it.
- **Spf** *(property)* - SPF, as DMARC judged it: pass or fail.

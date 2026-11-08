# DomainCounters

**Namespace:** `Anjal.Smtp`

rc.27 (MOM-10, DES-16 §15 point 6): counts kept per recipient domain, so the Monday summary can say how much of the spam refused at the door was meant for each organisation. Only a domain and a number: never an address or anything of the mail. At most 5,000 domains are counted, so a flood of made-up domains cannot grow it without end.

## Members

- **SpamRefused** *(field)* - Mail refused at the door as spam.
- **Increment** *(method)* - Count one for a domain.
- **Snapshot** *(method)* - Every count so far, by "kind|domain" (they only go up).

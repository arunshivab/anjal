# TlsReportRow

**Namespace:** `Anjal.Viewing`

One policy's part of a TLS report: how many deliveries to the domain were encrypted, and how many failed (rc.15, item 59).

## Members

- **#ctor** *(method)* - One policy's part of a TLS report: how many deliveries to the domain were encrypted, and how many failed (rc.15, item 59).
- **Domain** *(property)* - The domain the deliveries were to.
- **Failed** *(property)* - Sessions that failed.
- **Failures** *(property)* - Why they failed, for example "certificate-expired", joined with commas.
- **Policy** *(property)* - The policy checked: "sts" (MTA-STS), "tlsa" (DANE) or "no-policy-found".
- **Reporter** *(property)* - The receiver that sent the report, for example "Google Inc.".
- **Successful** *(property)* - Sessions that were encrypted as the policy asks.

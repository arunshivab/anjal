# VirusStats

**Namespace:** `Anjal.Mailbox`

What the virus scanner has done since it was switched on (rc.22, item 39), kept as the service record "virus" and shown in the Anjal console.

## Members

- **Kind** *(field)* - The service record kind.
- **ReadAsync** *(method)* - Read the record (fresh when there is none or the store keeps no records).
- **UpdateAsync** *(method)* - Change the record and save it.
- **Held** *(property)* - Mails held while the scanner was down.
- **Junked** *(property)* - Disk images sent to Junk.
- **LastDown** *(property)* - When the scanner was last found down.
- **LastSeen** *(property)* - When the scanner last answered.
- **Refused** *(property)* - Programs and scripts refused.
- **Removed** *(property)* - Viruses removed (the mail delivered without the file).
- **Scanned** *(property)* - Files scanned.
- **Since** *(property)* - Since when the counts run.
- **Version** *(property)* - The scanner's version line, as it last reported it.

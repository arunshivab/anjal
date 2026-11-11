# PatientInformation

**Namespace:** `Anjal.Mailbox`

rc.22 (item 50, design review 6k; owner 19 Oct 2026: personal information too): the warning in Compose. When a message to someone outside the organisation looks like it carries patient information - a record number, clinical words in the subject or body, or a file named like a report - the person is asked to check before it goes. A warning, never a block: the person decides.

## Members

- **Aadhaar** *(method)* - _(no description)_
- **BankAccount** *(method)* - _(no description)_
- **Pan** *(method)* - _(no description)_
- **PersonalSigns** *(method)* - Owner, 19 Oct 2026: what looks like someone's personal information - an Aadhaar, PAN or passport number, a phone number, bank details, pay, a password - in words, or none.
- **Phone** *(method)* - _(no description)_
- **RecordNumber** *(method)* - _(no description)_
- **Signs** *(method)* - What in a message looks like patient information: the reasons, in words, or none. Checked only for mail to an address outside the organisation.

# RuleSubject

**Namespace:** `Anjal.Mailbox`

The facts about an arriving message that rules can test.

## Members

- **Body** *(property)* - The text of the body.
- **From** *(property)* - The From header (name and address).
- **FromAddress** *(property)* - The sender's address, for group tests.
- **FromOutside** *(property)* - The sender's address is outside the recipient's organisation.
- **HasAttachment** *(property)* - It carries an attachment.
- **HasUnsubscribe** *(property)* - It carries a List-Unsubscribe header (a newsletter).
- **Subject** *(property)* - The subject.
- **To** *(property)* - The To and Cc headers.

# CategoryRuleRow

**Namespace:** `Anjal.Store`

"File mail from this sender under this category." Written when the reader assigns a category and asks for future mail to follow, and applied at delivery. Owned by one mailbox: categorising is a personal act even when the category is shared.

## Members

- **CategoryId** *(property)* - The category to apply.
- **CreatedAt** *(property)* - When it was created.
- **Id** *(property)* - Identifier assigned by the store.
- **MailboxId** *(property)* - The mailbox whose mail this rule files.
- **Pattern** *(property)* - Sender pattern: a full address (lab@example.com) or a domain (@example.com), matched the same way sender rules are.

# AddressBook

**Namespace:** `Anjal.Mailbox`

An organisation's extra addresses and group addresses (rc.22, item 41), kept as its "addresses" document. A shared mailbox is different: one Inbox several people open. A group address delivers a copy to each member's own Inbox.

## Members

- **Kind** *(field)* - The tenant document kind.
- **MaxMembers** *(field)* - At most this many members in a group.
- **AliasAt** *(method)* - The alias at an address, or null.
- **GroupAt** *(method)* - The group at an address, or null.
- **ReadAsync** *(method)* - Read an organisation's address book (empty when it has none).
- **WriteAsync** *(method)* - Save an organisation's address book.
- **Aliases** *(property)* - The extra addresses.
- **Groups** *(property)* - The group addresses.

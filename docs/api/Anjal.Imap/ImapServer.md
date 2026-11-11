# ImapServer

**Namespace:** `Anjal.Imap`

rc.28 (DES-20): Anjal's IMAP server - IMAP4rev1 on 993, for mail apps signed in with app passwords.

## Members

- **MaxPerAddress** *(field)* - Connections at once from one internet address (a household or office shares one).
- **#ctor** *(method)* - Construct.
- **DisposeAsync** *(method)* - _(no description)_
- **Enter** *(method)* - Count a person's session; false when they have too many.
- **Leave** *(method)* - A person's session ended.
- **Start** *(method)* - Start listening.
- **LocalPort** *(property)* - The port listened on (after ).

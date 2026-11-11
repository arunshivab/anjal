# ImapPullClient

**Namespace:** `Anjal.Imap`

rc.28 (DES-20 §5): the little IMAP client that copies a person's old mail from their previous mail server - sign in, list the folders, read each in UID order, a batch at a time. It only reads: it never changes, moves or deletes anything there (every FETCH is BODY.PEEK, every folder EXAMINEd).

## Members

- **MaxMessage** *(field)* - The largest message copied; a larger one is skipped.
- **ConnectAsync** *(method)* - Connect and read the greeting.
- **DateOf** *(method)* - _(no description)_
- **DisposeAsync** *(method)* - _(no description)_
- **FetchAsync** *(method)* - Read some messages of the open folder, never marking them read there.
- **FlagsOf** *(method)* - _(no description)_
- **ListAsync** *(method)* - The account's folders (selectable ones only).
- **ListLine** *(method)* - _(no description)_
- **LiteralAtEnd** *(method)* - _(no description)_
- **LoginAsync** *(method)* - Sign in.
- **SizeOf** *(method)* - _(no description)_
- **UidOf** *(method)* - _(no description)_
- **UidsAsync** *(method)* - Open a folder for reading only, and list its UIDs above .

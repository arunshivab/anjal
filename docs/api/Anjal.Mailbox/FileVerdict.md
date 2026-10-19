# FileVerdict

**Namespace:** `Anjal.Mailbox`

What was decided about a message's files before delivery (rc.22, item 39).

## Members

- **Clean** *(field)* - No file, or every file clean: deliver as it is.
- **Held** *(field)* - The scanner is down: the message is held until it can be checked.
- **Junk** *(field)* - A disk image: delivered to Junk.
- **Refused** *(field)* - A program or script: the whole message is refused (550), the sender told.
- **Removed** *(field)* - A virus: the file is removed and the message delivered without it.

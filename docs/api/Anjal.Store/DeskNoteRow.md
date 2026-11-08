# DeskNoteRow

**Namespace:** `Anjal.Store`

A note for a desk, never sent (rc.27, DES-17 §2.8).

## Members

- **At** *(property)* - When it was written.
- **Body** *(property)* - The text, sealed to the desk's key (plain only for a desk that is not encrypted).
- **Id** *(property)* - Its id.
- **ItemId** *(property)* - The item.
- **Mentions** *(property)* - The members it mentions, as ids separated by commas.
- **WithdrawnAt** *(property)* - When the writer withdrew it; null while it stands.
- **WriterId** *(property)* - Who wrote it.

# ChatMessageRow

**Namespace:** `Anjal.Store`

One chat message (rc.28); its words sealed to the members' keys and the organisation's.

## Members

- **Body** *(property)* - The words, sealed (base64url envelope) - or plain for an organisation that is not encrypted.
- **CreatedAt** *(property)* - When it was written.
- **Id** *(property)* - Its id.
- **RemovedAt** *(property)* - When its writer removed it; null while it stands.
- **RoomId** *(property)* - The chat.
- **SenderId** *(property)* - Who wrote it.

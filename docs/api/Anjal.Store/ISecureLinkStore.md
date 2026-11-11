# ISecureLinkStore

**Namespace:** `Anjal.Store`

rc.28 (DES-19): what secure links need from the store.

## Members

- **AddSecureLinkAsync** *(method)* - Keep a new link.
- **AddSecureLinkCodeAsync** *(method)* - Keep a code mailed for a link.
- **GetSecureLinkAsync** *(method)* - A link by its id.
- **GetSecureLinkByTokenAsync** *(method)* - A link by its token's hash.
- **ListSecureLinkCodesAsync** *(method)* - The codes of a link made since a time, newest first.
- **ListSecureLinksForMessageAsync** *(method)* - The links made for one sent mail, by its Message-ID.
- **PurgeEndedSecureLinksAsync** *(method)* - Remove the sealed copies of links that ended (expired, stopped or locked) before a time: the rows stay, for the sender's record, with no mail in them.
- **TakeSecureCodeMailAsync** *(method)* - Mark that a code is being mailed, only when none was mailed since , so two asks at once mail one code.
- **TakeSecureCodeTryAsync** *(method)* - Take one try at a code, before the code is compared, so tries made at once are all counted: the link's count goes up by one only while it is under and the link is not locked or stopped.
- **TakeSecureReplyAsync** *(method)* - Take the one reply of a visit that started at : true only when the link has had no reply since then, so a visit (or its cookie, sent again) gives one reply.
- **UpdateSecureLinkAsync** *(method)* - Record what happened to a link: the given fields are set when not null; counts are added.
- **UseSecureLinkCodeAsync** *(method)* - Use a code once: true only for the first caller.

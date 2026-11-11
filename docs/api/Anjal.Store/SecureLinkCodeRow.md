# SecureLinkCodeRow

**Namespace:** `Anjal.Store`

One code mailed for a secure link (rc.28, DES-19 §4).

## Members

- **CodeHmac** *(property)* - The code's keyed hash (HMAC-SHA256 under the link's token key), hex.
- **Id** *(property)* - Its id.
- **LinkId** *(property)* - The link.
- **MadeAt** *(property)* - When it was made.
- **UsedAt** *(property)* - When it was used; null while unused.

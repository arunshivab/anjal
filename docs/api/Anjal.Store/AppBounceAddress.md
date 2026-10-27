# AppBounceAddress

**Namespace:** `Anjal.Store`

rc.26 (DES-14 §17, D-229): the per-send return path of an application send, bounce+<send id>@<return domain>, the send id in lowercase base32 (26 characters, so that a server that changes the case of an address cannot break it).

## Members

- **Prefix** *(field)* - The local part's start.
- **Decode** *(method)* - The send id of 26 base32 characters, or null.
- **Encode** *(method)* - A send id as 26 lowercase base32 characters.
- **For** *(method)* - The return path of a send.
- **IsBounceAddress** *(method)* - Whether an address's local part is a return path's.
- **SendOf** *(method)* - The send a return path names, or null when it is not one.

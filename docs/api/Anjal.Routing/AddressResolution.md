# AddressResolution

**Namespace:** `Anjal.Routing`

Result of splitting an SMTP recipient address into its routing components.

## Members

- **Parse** *(method)* - Parse a recipient address into local-part / tag / domain. Sub-addressing uses "+" as the separator per RFC 5233 - the portion between the local-part and "+" is the routable key; the portion between "+" and "@" is the tag.
- **Domain** *(property)* - The domain portion of the address.
- **LocalPart** *(property)* - The local-part with any sub-address tag removed.
- **Recipient** *(property)* - The recipient as received over SMTP, in lowercase.
- **Tag** *(property)* - The "+tag" portion if present, otherwise empty.

# DkimSigningOptions

**Namespace:** `Anjal.Dkim`

Configuration controlling how messages are signed.

## Members

- **BodyCanon** *(property)* - Body canonicalization. Default: Relaxed.
- **HeaderCanon** *(property)* - Header canonicalization. Default: Relaxed.
- **IncludeBodyLength** *(property)* - When true, the l= body-length tag is included. Recommended OFF because l= permits content to be appended to the body without breaking the signature. Default: false.
- **SignedHeaders** *(property)* - Which headers to include in the signature, in order. The From header is required by RFC 6376 and is always included even if omitted here. Defaults cover the headers that matter for authenticity in transactional mail.

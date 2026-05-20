# DkimDetail

**Namespace:** `Anjal.Auth`

Detailed result of a DKIM signature verification. Anjal verifies the first DKIM-Signature header it encounters; messages with multiple signatures will have only the first reported here.

## Members

- **Algorithm** *(property)* - Algorithm used (e.g. rsa-sha256), or empty if no signature.
- **Domain** *(property)* - The signing domain (d= tag), or empty if no signature.
- **Explanation** *(property)* - Human-readable explanation suitable for the Authentication-Results comment.
- **Result** *(property)* - The verdict.
- **Selector** *(property)* - The selector (s= tag), or empty if no signature.

# BreakGlassLine

**Namespace:** `Anjal.Store`

rc.25 (DES-15 §6.3, OPS-16): the one line that carries Anjal's break-glass key to the server's settings (ANJAL_BREAKGLASS_KEY): its public half, and its private half wrapped with the secret that exists only as the three paper shares. The line alone opens nothing. anjal-breakglass-v1.<public, base64url>.<wrapped private, base64url>

## Members

- **Setting** *(field)* - The setting that holds the line.
- **Read** *(method)* - Read the line; null when it is not one.
- **Write** *(method)* - Write the line.

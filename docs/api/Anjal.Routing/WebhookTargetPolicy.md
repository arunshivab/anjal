# WebhookTargetPolicy

**Namespace:** `Anjal.Routing`

Where webhooks may be sent. By default only to https URLs on public addresses: a webhook URL is admin-supplied, but a server that will POST mail contents to any address it is told is a tool for reaching internal services (SSRF), so the default is narrow and each widening is an explicit setting. The address check runs at connect time as well as at registration, on the address actually being connected to. Checking only the name when the rule is saved would let DNS be changed afterwards to point at an internal address (DNS rebinding).

## Members

- **MaxUrlLength** *(field)* - Longest webhook URL accepted.
- **CreateClient** *(method)* - An for delivering webhooks under this policy: redirects are not followed (a public endpoint could otherwise bounce the request to an internal one), and every connection is checked against on the address actually dialled.
- **FromEnvironment** *(method)* - Read the policy from ANJAL_WEBHOOK_ALLOW_HTTP and ANJAL_WEBHOOK_ALLOW_PRIVATE (both default false).
- **IsAllowedAddress** *(method)* - Whether a connection to this address is allowed.
- **IsPublic** *(method)* - Whether an address is routable on the public internet: not loopback, private, link-local, unique-local, carrier-grade NAT, multicast, unspecified or documentation space.
- **Validate** *(method)* - Why a URL is not acceptable, or null when it is. Checks the shape of the URL and, when the host is an address literal, the address. Host names are checked again at connect time.
- **AllowHttp** *(property)* - Allow plain http (for a receiver on the same private network).
- **AllowPrivateAddresses** *(property)* - Allow loopback and private (RFC 1918, RFC 4193, link-local) addresses. Needed when the receiving application runs on the same host or LAN, as SIGMA and Lipi deployments may.

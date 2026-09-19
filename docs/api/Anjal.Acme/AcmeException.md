# AcmeException

**Namespace:** `Anjal.Acme`

Thrown when the ACME server returns a problem document or an unexpected response.

## Members

- **#ctor** *(method)* - Construct.
- **#ctor** *(method)* - Construct with a message.
- **#ctor** *(method)* - Construct with a message and inner exception.
- **ProblemType** *(property)* - The RFC 7807 problem type (e.g. urn:ietf:params:acme:error:badNonce), if any.
- **StatusCode** *(property)* - HTTP status, if any.

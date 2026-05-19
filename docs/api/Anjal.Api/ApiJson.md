# ApiJson

**Namespace:** `Anjal.Api`

JSON codec used by the API. Single source of truth for serialization options so request and response bodies stay symmetric.

## Members

- **Options** *(field)* - The JSON options the API uses. Camel-case property names, ignore null values on output, case-insensitive on input.
- **Deserialize** *(method)* - Deserialise a JSON string into a value of the given type. Returns on empty input.
- **Serialize** *(method)* - Serialise a value to a UTF-8 string.

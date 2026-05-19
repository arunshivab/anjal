# HeaderCollection

**Namespace:** `Anjal.Mime`

An ordered, case-insensitive collection of MIME header fields. Headers can occur more than once with the same name (e.g. multiple Received headers); this collection preserves both order and duplicates.

## Members

- **Add** *(method)* - Append a header. Existing headers with the same name are not removed - use for replace-or-add semantics.
- **Add** *(method)* - Append a header given its name and value. Equivalent to Add(new MimeHeader(name, value)).
- **Contains** *(method)* - Whether the collection contains at least one header with the given name (case-insensitive).
- **Get** *(method)* - Get the value of the first header matching the given name (case-insensitive), or if no such header exists.
- **GetAll** *(method)* - Get all values of headers matching the given name (case-insensitive), in the order they appear.
- **GetEnumerator** *(method)* - _(no description)_
- **Remove** *(method)* - Remove all headers with the given name (case-insensitive).
- **Set** *(method)* - Set a header to a single value. Removes all existing headers with the same name (case-insensitive) and appends a single new one.
- **Count** *(property)* - The number of header fields in the collection.

; Unshipped analyzer release

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
CLNK001 | AnyProtocol | Error | Contract types must be interfaces.
CLNK002 | AnyProtocol | Error | Generic, overloaded, and by-reference methods are unsupported.
CLNK003 | AnyProtocol | Error | Methods accept at most one reference-type payload.
CLNK004 | AnyProtocol | Error | CancellationToken must be unique and final.
CLNK005 | AnyProtocol | Error | Return types must map to a supported operation.
CLNK006 | AnyProtocol | Error | Partition key properties must be unique, readable, and non-indexed.
CLNK007 | AnyProtocol | Error | Registrations must name a closed contract directly so source generation can discover them.
CLNK008 | AnyProtocol | Error | Contract properties, events, and static methods are unsupported.
CLNK009 | AnyProtocol | Error | Event registrations must use a closed event and a matching event consumer.

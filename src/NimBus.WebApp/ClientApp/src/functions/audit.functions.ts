// Audit types arrive as the values api-spec.yaml declares — camelCase. They were
// PascalCase on the wire for as long as the server serialized CLR names instead of
// the contract, so both spellings are accepted: an audit trail is history, and rows
// written before the server was corrected still carry the old spelling.
//
// Derived rather than enumerated: a hand-kept map silently leaks raw camelCase for
// every audit type nobody remembered to add — which is how `manageSubscription`
// reached the Action column verbatim.
export function formatAuditType(type: string | undefined): string {
  if (!type) return "-";
  const camel = type[0].toLowerCase() + type.slice(1);
  const words = camel.replace(/([A-Z])/g, " $1").toLowerCase().trim();
  return words[0].toUpperCase() + words.slice(1);
}

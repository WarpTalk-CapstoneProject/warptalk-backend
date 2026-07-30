# WarpTalk event contracts

`catalog.json` is the source of truth for durable inter-service events. Active
events use the standard envelope in `v1/event-envelope.schema.json`.

Compatibility rules:

- Adding an optional payload field is backward compatible.
- Making an optional field required, removing a field, narrowing an enum, or
  changing a field type is breaking.
- A breaking change must be published under a new version directory and must
  use a new `schema_version`.
- Producers may dual-publish during migration. Consumers must declare the
  versions they accept in the catalog before deployment.
- Deprecated versions remain readable for at least one release window and
  until all listed consumers have migrated.

Redis compatibility streams retain their existing flat fields, but now also
carry the canonical envelope metadata and a JSON `payload` field. New durable
consumers should use RabbitMQ and the catalogued envelope.

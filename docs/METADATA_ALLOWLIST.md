# Metadata generation allowlist

Production message IDs and SentAt values must be created through IMessageEnvelopeFactory.

The following direct system calls are intentional infrastructure identities and are reviewed by the architecture test:

- DefaultMessageIdGenerator.cs: the default message-ID strategy itself calls Guid.NewGuid().
- RequestReplyEngine.cs and StreamEngine.cs: reply-channel instance identities.
- KafkaProtocolOptions.cs and KafkaMessagingProtocol.cs: client, consumer-group, and subscription instance identities.
- RabbitMqProtocolOptions.cs and RabbitMqMessagingProtocol.cs: client and subscription instance identities.
- ZeroMqMessagingProtocol.cs: the ZeroMQ socket identity.
- DefaultDateTimeProvider.cs: the default UTC clock implementation.

Payload IDs, correlation IDs, SentAt, deadlines, response IDs, and fault IDs are not on this list.

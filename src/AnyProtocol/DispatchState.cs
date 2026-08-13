using AnyProtocol.Configuration;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Holds typed state shared by the dispatcher and its internal pipeline stages.
/// </summary>
internal sealed class DispatchState
{
    public DispatchState(
        ServerRegistration? registration,
        EventRegistration? eventRegistration,
        IMessagingProtocol? transport)
    {
        Registration = registration;
        EventRegistration = eventRegistration;
        Transport = transport;
    }

    public ServerRegistration? Registration { get; }

    public EventRegistration? EventRegistration { get; }

    public IMessagingProtocol? Transport { get; }

}

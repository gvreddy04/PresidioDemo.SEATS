using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PresidioDemo.Messaging.Abstractions;
using PresidioDemo.Messaging.Models;
using PresidioDemo.Messaging.Solace.Connection;
using PresidioDemo.Messaging.Solace.Models;
using SolaceSystems.Solclient.Messaging;

namespace PresidioDemo.Messaging.Solace.Listener;

/// <summary>
/// Subscribes the shared session to a topic and receives each published message as a direct copy, through a dispatch target so
/// only this subscription's messages reach it (QF-26111602). Nothing is acknowledged and no queue is involved.
/// </summary>
/// <param name="connection">The shared Solace connection.</param>
public sealed class SolaceTopicSubscriber(SolaceConnection connection) : IMessageSubscriber
{

    #region Methods

    /// <summary>
    /// Adds the topic subscription, streams its messages until cancellation, then removes the subscription.
    /// </summary>
    /// <param name="topic">The topic name; Solace wildcards such as <c>airline/seats/&gt;</c> are allowed.</param>
    /// <param name="cancellationToken">The token that stops receiving.</param>
    /// <returns>The received messages in arrival order.</returns>
    /// <exception cref="InvalidOperationException">The subscription could not be added.</exception>
    public async IAsyncEnumerable<InboundMessage> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var received = Channel.CreateUnbounded<InboundMessage>(new UnboundedChannelOptions { SingleReader = true });
        var session = connection.Session();
        IDispatchTarget target;
        try
        {
            target = session.CreateDispatchTarget(ContextFactory.Instance.CreateTopic(topic), (_, e) => received.Writer.TryWrite(Copy(e.Message, topic)));
            var result = session.Subscribe(target, SubscribeFlag.WaitForConfirm, null);
            if (result != ReturnCode.SOLCLIENT_OK)
            {
                throw new InvalidOperationException($"Cannot subscribe to Solace topic {topic}: {result}");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Cannot subscribe to Solace topic {topic}: {ex.Message}", ex);
        }
        try
        {
            await foreach (var message in received.Reader.ReadAllAsync(cancellationToken))
            {
                yield return message;
            }
        }
        finally
        {
            try { session.Unsubscribe(target, 0, null); }
            catch (Exception) { /* The session is closing; the broker drops its subscriptions with it. */ }
        }
    }

    /// <summary>
    /// Copies a native message and releases it.
    /// </summary>
    /// <param name="message">The native message.</param>
    /// <param name="topic">The subscribed topic.</param>
    /// <returns>The managed copy, whose completion does nothing.</returns>
    private static InboundMessage Copy(IMessage message, string topic)
    {
        using (message)
        {
            return SolaceMessageMapper.ToInbound(message, topic, _ => ValueTask.CompletedTask);
        }
    }

    #endregion
}

using System.Diagnostics;
using StreamCart.BuildingBlocks.Messaging;

namespace StreamCart.BuildingBlocks.Persistence;

internal sealed class EfOutbox<TContext>(TContext db, ServiceIdentity identity, TimeProvider clock) : IOutbox
    where TContext : MessagingDbContext
{
    public void Enqueue(IIntegrationMessage message)
    {
        var envelope = MessageEnvelope.Create(message, identity.Name, clock, Activity.Current?.Id);
        db.OutboxMessages.Add(OutboxMessage.From(envelope));
    }
}

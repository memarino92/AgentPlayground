using System.Collections.Concurrent;
using System.Threading.Channels;
using AgentPlayground.Contracts.Events;

namespace PersonalAgent.Web.Services;

// Carries invalidations only. Each circuit rereads data through the authorized API.
public sealed class CoachCallUpdates
{
    private readonly ConcurrentDictionary<Guid, Subscription> Subscriptions = new();

    public Subscription Subscribe(string? ProfileId, Guid? UploadId = null)
    {
        var Id = Guid.NewGuid();
        var Subscription = new Subscription(ProfileId, UploadId, () => Subscriptions.TryRemove(Id, out _));
        Subscriptions[Id] = Subscription;
        return Subscription;
    }

    public void Notify(CoachCallStatusChangedEvent Change)
    {
        foreach (var Subscription in Subscriptions.Values) Subscription.Notify(Change);
    }

    public sealed class Subscription(string? ProfileId, Guid? UploadId, Action Remove) : IDisposable
    {
        private readonly Channel<bool> Changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        public ChannelReader<bool> Reader => Changes.Reader;

        internal void Notify(CoachCallStatusChangedEvent Change)
        {
            if (ProfileId is not null && !string.Equals(ProfileId, Change.ProfileId, StringComparison.Ordinal)) return;
            if (UploadId is not null && UploadId != Change.UploadId) return;
            Changes.Writer.TryWrite(true);
        }

        public void Dispose()
        {
            Remove();
            Changes.Writer.TryComplete();
        }
    }
}

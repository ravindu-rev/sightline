using System.Threading.Channels;

namespace Co.Revington.Sightline.Live;

/// Fans out server-sent events to every connected browser tab.
internal sealed class EventHub
{
    readonly List<Channel<string>> _subscribers = [];

    public Channel<string> Subscribe()
    {
        var channel = Channel.CreateUnbounded<string>();
        lock (_subscribers) _subscribers.Add(channel);
        return channel;
    }

    public void Unsubscribe(Channel<string> channel)
    {
        lock (_subscribers) _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    public void Publish(string name, string json)
    {
        var message = $"event: {name}\ndata: {json}\n\n";
        lock (_subscribers)
            foreach (var s in _subscribers) s.Writer.TryWrite(message);
    }
}

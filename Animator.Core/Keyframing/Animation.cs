using Animator.Core.Rigging;

namespace Animator.Core.Keyframing;

public class Animation
{
    public string Name { get; set; } = "";
    public float Duration { get; set; }
    public bool Loop { get; set; }
    public List<Timeline> Timelines { get; } = new();

    public void Apply(Skeleton skeleton, float time)
    {
        // Only wrap when actually outside the range, so the last frame stays the last frame
        if (Loop && Duration > 0 && (time > Duration || time < 0))
        {
            time %= Duration;
            if (time < 0) time += Duration;
        }
        else
        {
            time = Math.Clamp(time, 0, Duration);
        }

        foreach (var timeline in Timelines)
            timeline.Apply(skeleton, time);
    }

    public T? Find<T>(string target) where T : Timeline =>
        Timelines.OfType<T>().FirstOrDefault(t => t.Target == target);

    public T GetOrCreate<T>(string target, Func<string, T> create) where T : Timeline
    {
        var tl = Find<T>(target);
        if (tl == null)
        {
            tl = create(target);
            Timelines.Add(tl);
        }
        return tl;
    }

    // The event row, if this animation has any events
    public EventTimeline? Events => Timelines.OfType<EventTimeline>().FirstOrDefault();

    // Created on first use, and always kept as the first row so it's easy to find
    public EventTimeline GetOrCreateEvents()
    {
        var events = Events;
        if (events == null)
        {
            events = new EventTimeline();
            Timelines.Insert(0, events);
        }
        return events;
    }

    // The draw order row, if this animation changes the order parts are drawn in
    public DrawOrderTimeline? DrawOrder => Timelines.OfType<DrawOrderTimeline>().FirstOrDefault();

    // Created on first use, near the top (just under the events)
    public DrawOrderTimeline GetOrCreateDrawOrder()
    {
        var order = DrawOrder;
        if (order == null)
        {
            order = new DrawOrderTimeline();
            Timelines.Insert(Timelines.Count > 0 && Timelines[0] is EventTimeline ? 1 : 0, order);
        }
        return order;
    }
}
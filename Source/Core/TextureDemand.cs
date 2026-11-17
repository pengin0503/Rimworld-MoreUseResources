using System;
using System.Collections.Generic;

namespace MoreUseResources.Core;

public sealed class TextureDemand<T> where T : notnull
{
    private readonly int capacity;
    private readonly PriorityQueue<T, int> candidates = new();
    private readonly HashSet<T> seen = new();

    public TextureDemand(int capacity) => this.capacity = Math.Max(1, capacity);

    public void Observe(T texture, int total, int own)
    {
        if (total <= own || !seen.Add(texture)) return;
        int count = total - own;
        if (candidates.Count >= capacity)
        {
            candidates.TryPeek(out _, out int lowest);
            if (count <= lowest)
            {
                seen.Remove(texture);
                return;
            }
            seen.Remove(candidates.Dequeue());
        }
        candidates.Enqueue(texture, count);
    }

    /// <summary>The current ranking with its counts, best first, without consuming it.</summary>
    public List<(T Item, int Count)> PeekRanked()
    {
        var ranked = new List<(T Item, int Count)>(candidates.Count);
        foreach ((T element, int priority) in candidates.UnorderedItems) ranked.Add((element, priority));
        ranked.Sort((a, b) => b.Count.CompareTo(a.Count));
        return ranked;
    }

    public Queue<T> TakeRanked()
    {
        var ranked = new T[candidates.Count];
        for (int i = ranked.Length - 1; i >= 0; i--) ranked[i] = candidates.Dequeue();
        seen.Clear();
        return new Queue<T>(ranked);
    }

    public void Clear()
    {
        candidates.Clear();
        seen.Clear();
    }
}

using System;
using System.Collections.Generic;

namespace SignalRouter.Kernel
{
    /// <summary>
    /// A deadline-ordered index (binary min-heap with a position map): due keys
    /// pop in O(log n) instead of a full scan per pump, and removal is a real
    /// O(log n) deletion — a cancelled entry never lingers as a tombstone, so an
    /// entry with a far-future deadline (waits armed with no timeout) cannot
    /// accumulate. Ordering is fully deterministic: deadline first, insertion
    /// sequence second. Pump-thread only.
    /// </summary>
    internal sealed class DeadlineIndex<TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        private readonly Dictionary<TKey, int> positions = new Dictionary<TKey, int>();
        private TKey[] keys;
        private long[] deadlines;
        private long[] sequences;
        private int count;
        private long nextSequence;

        internal DeadlineIndex(int initialCapacity = 16)
        {
            keys = new TKey[initialCapacity];
            deadlines = new long[initialCapacity];
            sequences = new long[initialCapacity];
        }

        internal int Count => count;

        /// <summary>Adds a key; the caller guarantees it is not present (kernel invariant).</summary>
        internal void Add(TKey key, long deadline)
        {
            if (count == keys.Length)
            {
                Array.Resize(ref keys, count * 2);
                Array.Resize(ref deadlines, count * 2);
                Array.Resize(ref sequences, count * 2);
            }

            keys[count] = key;
            deadlines[count] = deadline;
            sequences[count] = nextSequence++;
            positions.Add(key, count);
            SiftUp(count);
            count++;
        }

        /// <summary>Real deletion by key; answers whether the key was present.</summary>
        internal bool Remove(TKey key)
        {
            if (!positions.TryGetValue(key, out var index))
            {
                return false;
            }

            RemoveAt(index);
            return true;
        }

        /// <summary>Pops the most-overdue entry when one is due at <paramref name="now"/>.</summary>
        internal bool TryPopExpired(long now, out TKey key)
        {
            if (count == 0 || deadlines[0] > now)
            {
                key = default!;
                return false;
            }

            key = keys[0];
            RemoveAt(0);
            return true;
        }

        private void RemoveAt(int index)
        {
            positions.Remove(keys[index]);
            count--;
            if (index == count)
            {
                keys[count] = default!;
                return;
            }

            keys[index] = keys[count];
            deadlines[index] = deadlines[count];
            sequences[index] = sequences[count];
            keys[count] = default!;
            positions[keys[index]] = index;

            // The moved entry can violate either direction.
            if (!SiftUp(index))
            {
                SiftDown(index);
            }
        }

        private bool SiftUp(int index)
        {
            var moved = false;
            while (index > 0)
            {
                var parent = (index - 1) / 2;
                if (!Precedes(index, parent))
                {
                    break;
                }

                Swap(index, parent);
                index = parent;
                moved = true;
            }

            return moved;
        }

        private void SiftDown(int index)
        {
            while (true)
            {
                var left = index * 2 + 1;
                if (left >= count)
                {
                    return;
                }

                var right = left + 1;
                var smallest = right < count && Precedes(right, left) ? right : left;
                if (!Precedes(smallest, index))
                {
                    return;
                }

                Swap(smallest, index);
                index = smallest;
            }
        }

        private bool Precedes(int left, int right) =>
            deadlines[left] < deadlines[right] ||
            (deadlines[left] == deadlines[right] && sequences[left] < sequences[right]);

        private void Swap(int left, int right)
        {
            (keys[left], keys[right]) = (keys[right], keys[left]);
            (deadlines[left], deadlines[right]) = (deadlines[right], deadlines[left]);
            (sequences[left], sequences[right]) = (sequences[right], sequences[left]);
            positions[keys[left]] = left;
            positions[keys[right]] = right;
        }
    }
}

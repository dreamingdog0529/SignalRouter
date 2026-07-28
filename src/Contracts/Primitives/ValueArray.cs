using System;
using System.Collections;
using System.Collections.Generic;

namespace SignalRouter.Contracts
{
    /// <summary>
    /// An immutable, defensively-copied read-only list whose equality and hash code
    /// compare elements in order — the universal aggregate of the v2 contract
    /// surface. A single exact-sized array backs it; <c>default</c> is the empty
    /// list (ADR 0013 — "missing versus empty" is expressed by the surrounding
    /// type, never by a distinguished default). <c>foreach</c> uses a
    /// non-allocating struct enumerator; interface enumeration boxes and is the
    /// slow path. Elements may be value types; null elements are rejected for
    /// reference types.
    /// </summary>
    public readonly struct ValueArray<T> : IReadOnlyList<T>, IEquatable<ValueArray<T>>
    {
        private readonly T[]? items; // null ⇔ empty ⇔ default

        private ValueArray(T[] items)
        {
            this.items = items;
        }

        public static ValueArray<T> Empty => default;

        public int Count => items == null ? 0 : items.Length;

        public T this[int index]
        {
            get
            {
                var array = items;
                if (array == null || (uint)index >= (uint)array.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return array[index];
            }
        }

        /// <summary>The elements as a span — the sort/search/compare fast path.</summary>
        public ReadOnlySpan<T> AsSpan() => items;

        /// <summary>Copies <paramref name="source"/>, rejecting null elements.</summary>
        public static ValueArray<T> From(IEnumerable<T> source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            T[] copy;
            if (source is ICollection<T> collection)
            {
                if (collection.Count == 0)
                {
                    return default;
                }

                copy = new T[collection.Count];
                collection.CopyTo(copy, 0);
            }
            else
            {
                var buffer = new List<T>();
                foreach (var item in source)
                {
                    buffer.Add(item);
                }

                if (buffer.Count == 0)
                {
                    return default;
                }

                copy = buffer.ToArray();
            }

            foreach (var item in copy)
            {
                if (item == null)
                {
                    throw new ArgumentException(
                        "ValueArray elements must not be null.", nameof(source));
                }
            }

            return new ValueArray<T>(copy);
        }

        public Enumerator GetEnumerator() => new Enumerator(items);

        IEnumerator<T> IEnumerable<T>.GetEnumerator() =>
            ((IEnumerable<T>)(items ?? Array.Empty<T>())).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() =>
            (items ?? Array.Empty<T>()).GetEnumerator();

        public bool Equals(ValueArray<T> other)
        {
            var left = items;
            var right = other.items;
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            var count = left == null ? 0 : left.Length;
            if (count != (right == null ? 0 : right.Length))
            {
                return false;
            }

            var comparer = EqualityComparer<T>.Default;
            for (var i = 0; i < count; i++)
            {
                if (!comparer.Equals(left![i], right![i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is ValueArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            var comparer = EqualityComparer<T>.Default;
            var hash = 17;
            var array = items;
            if (array != null)
            {
                foreach (var item in array)
                {
                    hash = ContractGrammar.CombineHashes(
                        hash, item == null ? 0 : comparer.GetHashCode(item));
                }
            }

            return hash;
        }

        public static bool operator ==(ValueArray<T> left, ValueArray<T> right) => left.Equals(right);

        public static bool operator !=(ValueArray<T> left, ValueArray<T> right) => !left.Equals(right);

        /// <summary>Non-allocating array walker; <c>default</c> enumerates nothing.</summary>
        public struct Enumerator
        {
            private readonly T[]? items;
            private int index;

            internal Enumerator(T[]? items)
            {
                this.items = items;
                index = -1;
            }

            public T Current => items![index];

            public bool MoveNext()
            {
                var array = items;
                if (array == null)
                {
                    return false;
                }

                var next = index + 1;
                if (next >= array.Length)
                {
                    return false;
                }

                index = next;
                return true;
            }
        }
    }
}

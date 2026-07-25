using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SignalRouter.V2.Contracts
{
    /// <summary>
    /// An immutable, defensively-copied read-only list whose equality and hash code
    /// compare elements in order. The evidence-cut aggregates use it so that value
    /// equality never observes caller-side mutation. Unlike v1's
    /// <c>EquatableList</c>, elements may be value types; null elements are rejected
    /// for reference types.
    /// </summary>
    public sealed class ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
    {
        private static readonly ValueList<T> EmptyInstance =
            new ValueList<T>(new List<T>());

        private readonly ReadOnlyCollection<T> items;

        private ValueList(List<T> items)
        {
            this.items = new ReadOnlyCollection<T>(items);
        }

        public static ValueList<T> Empty => EmptyInstance;

        public int Count => items.Count;

        public T this[int index] => items[index];

        /// <summary>Copies <paramref name="source"/>, rejecting null elements.</summary>
        public static ValueList<T> From(IEnumerable<T> source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var copy = new List<T>();
            foreach (var item in source)
            {
                if (item == null)
                {
                    throw new ArgumentException(
                        "ValueList elements must not be null.", nameof(source));
                }

                copy.Add(item);
            }

            return copy.Count == 0 ? EmptyInstance : new ValueList<T>(copy);
        }

        public IEnumerator<T> GetEnumerator()
        {
            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return items.GetEnumerator();
        }

        public bool Equals(ValueList<T>? other)
        {
            if (other == null || other.Count != Count)
            {
                return false;
            }

            var comparer = EqualityComparer<T>.Default;
            for (var i = 0; i < Count; i++)
            {
                if (!comparer.Equals(items[i], other.items[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as ValueList<T>);
        }

        public override int GetHashCode()
        {
            var comparer = EqualityComparer<T>.Default;
            var hash = 17;
            foreach (var item in items)
            {
                hash = ContractGrammar.CombineHashes(
                    hash, item == null ? 0 : comparer.GetHashCode(item));
            }

            return hash;
        }
    }
}

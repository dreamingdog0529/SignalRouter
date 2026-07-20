using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SignalRouter
{
    /// <summary>
    /// An immutable, defensively-copied read-only list whose equality and hash code
    /// compare elements in order. Centralizes the "validated immutable collection"
    /// mechanism shared by the interaction value types. The <c>class</c> constraint
    /// exists because the factories reject null elements.
    /// </summary>
    public sealed class EquatableList<T> : IReadOnlyList<T>, IEquatable<EquatableList<T>>
        where T : class
    {
        private static readonly EquatableList<T> EmptyInstance =
            new EquatableList<T>(new List<T>());

        private readonly ReadOnlyCollection<T> items;

        private EquatableList(List<T> items)
        {
            this.items = new ReadOnlyCollection<T>(items);
        }

        public static EquatableList<T> Empty
        {
            get { return EmptyInstance; }
        }

        public int Count
        {
            get { return items.Count; }
        }

        public T this[int index]
        {
            get { return items[index]; }
        }

        /// <summary>Copies <paramref name="source"/>, rejecting null elements.</summary>
        internal static EquatableList<T> Create(
            IEnumerable<T> source,
            string parameterName,
            string nullElementMessage)
        {
            var copy = CopyNonNull(source, parameterName, nullElementMessage);
            return copy.Count == 0 ? EmptyInstance : new EquatableList<T>(copy);
        }

        /// <summary>
        /// Copies <paramref name="source"/>, rejecting null elements and duplicate keys,
        /// then orders the result by key using an ordinal comparison.
        /// </summary>
        internal static EquatableList<T> CreateSortedUniqueByKey(
            IEnumerable<T> source,
            string parameterName,
            Func<T, string> keySelector,
            string nullElementMessage,
            string duplicateKeyMessage)
        {
            if (source == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            var copy = new List<T>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in source)
            {
                if (item == null)
                {
                    throw new ArgumentException(nullElementMessage, parameterName);
                }

                if (!keys.Add(keySelector(item)))
                {
                    throw new ArgumentException(duplicateKeyMessage, parameterName);
                }

                copy.Add(item);
            }

            copy.Sort((left, right) =>
                StringComparer.Ordinal.Compare(keySelector(left), keySelector(right)));
            return copy.Count == 0 ? EmptyInstance : new EquatableList<T>(copy);
        }

        /// <summary>
        /// Wraps an already-validated list, taking ownership of it without copying.
        /// The caller must not mutate the list afterwards.
        /// </summary>
        internal static EquatableList<T> CreateOwned(List<T> items)
        {
            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            return items.Count == 0 ? EmptyInstance : new EquatableList<T>(items);
        }

        public IEnumerator<T> GetEnumerator()
        {
            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return items.GetEnumerator();
        }

        public bool Equals(EquatableList<T>? other)
        {
            return other != null && InteractionContract.SequenceEqual(this, other);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as EquatableList<T>);
        }

        public override int GetHashCode()
        {
            return InteractionContract.GetSequenceHashCode(this);
        }

        private static List<T> CopyNonNull(
            IEnumerable<T> source,
            string parameterName,
            string nullElementMessage)
        {
            if (source == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            var copy = new List<T>();
            foreach (var item in source)
            {
                if (item == null)
                {
                    throw new ArgumentException(nullElementMessage, parameterName);
                }

                copy.Add(item);
            }

            return copy;
        }
    }
}

﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#pragma warning disable CA2208
#pragma warning disable CS8632

// ReSharper disable ALL

namespace NativeCollections
{
    /// <summary>
    ///     Stackalloc dictionary
    /// </summary>
    /// <typeparam name="TKey">Type</typeparam>
    /// <typeparam name="TValue">Type</typeparam>
    [StructLayout(LayoutKind.Sequential)]
    [StackallocCollection(FromType.Standard)]
    public unsafe struct StackallocDictionary<TKey, TValue> : IReadOnlyCollection<KeyValuePair<TKey, TValue>> where TKey : unmanaged, IEquatable<TKey> where TValue : unmanaged
    {
        /// <summary>
        ///     Buckets
        /// </summary>
        private int* _buckets;

        /// <summary>
        ///     Entries
        /// </summary>
        private Entry* _entries;

        /// <summary>
        ///     BucketsLength
        /// </summary>
        private int _bucketsLength;

        /// <summary>
        ///     EntriesLength
        /// </summary>
        private int _entriesLength;

        /// <summary>
        ///     FastModMultiplier
        /// </summary>
        private ulong _fastModMultiplier;

        /// <summary>
        ///     Count
        /// </summary>
        private int _count;

        /// <summary>
        ///     FreeList
        /// </summary>
        private int _freeList;

        /// <summary>
        ///     FreeCount
        /// </summary>
        private int _freeCount;

        /// <summary>
        ///     Version
        /// </summary>
        private int _version;

        /// <summary>
        ///     Is empty
        /// </summary>
        public readonly bool IsEmpty => _count - _freeCount == 0;

        /// <summary>
        ///     Count
        /// </summary>
        public readonly int Count => _count - _freeCount;

        /// <summary>
        ///     Capacity
        /// </summary>
        public readonly int Capacity => _entriesLength;

        /// <summary>
        ///     Keys
        /// </summary>
        public KeyCollection Keys => new(Unsafe.AsPointer(ref this));

        /// <summary>
        ///     Values
        /// </summary>
        public ValueCollection Values => new(Unsafe.AsPointer(ref this));

        /// <summary>
        ///     Get byte count
        /// </summary>
        /// <param name="capacity">Capacity</param>
        /// <returns>Byte count</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetByteCount(int capacity)
        {
            var size = HashHelpers.GetPrime(capacity);
            var alignment = (uint)Math.Max(NativeMemoryAllocator.AlignOf<int>(), NativeMemoryAllocator.AlignOf<Entry>());
            var bucketsByteCount = (uint)NativeMemoryAllocator.AlignUp((nuint)(size * sizeof(int)), alignment);
            return (int)(bucketsByteCount + size * sizeof(Entry) + alignment - 1);
        }

        /// <summary>
        ///     Structure
        /// </summary>
        /// <param name="buffer">Buffer</param>
        /// <param name="capacity">Capacity</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [MustBeZeroed("Span<byte> buffer")]
        public StackallocDictionary(Span<byte> buffer, int capacity)
        {
            var size = HashHelpers.GetPrime(capacity);
            _freeList = -1;
            var alignment = (uint)Math.Max(NativeMemoryAllocator.AlignOf<int>(), NativeMemoryAllocator.AlignOf<Entry>());
            var bucketsByteCount = (uint)NativeMemoryAllocator.AlignUp((nuint)(size * sizeof(int)), alignment);
            _buckets = (int*)NativeArray<byte>.Create(buffer, alignment).Buffer;
            _entries = UnsafeHelpers.AddByteOffset<Entry>(_buckets, (nint)bucketsByteCount);
            _bucketsLength = size;
            _entriesLength = size;
            _fastModMultiplier = sizeof(nint) == 8 ? HashHelpers.GetFastModMultiplier((uint)size) : 0;
            _count = 0;
            _freeCount = 0;
            _version = 0;
        }

        /// <summary>
        ///     Clear
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            var count = _count;
            if (count > 0)
            {
                Unsafe.InitBlockUnaligned(ref Unsafe.AsRef<byte>(_buckets), 0, (uint)(count * sizeof(int)));
                Unsafe.InitBlockUnaligned(ref Unsafe.AsRef<byte>(_entries), 0, (uint)(count * sizeof(Entry)));
                _count = 0;
                _freeList = -1;
                _freeCount = 0;
            }
        }

        /// <summary>
        ///     Try add
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public InsertResult TryAdd(in TKey key, in TValue value) => TryInsertNone(key, value);

        /// <summary>
        ///     Try insert
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public InsertResult TryInsert(in TKey key, in TValue value) => TryInsertOverwriteExisting(key, value);

        /// <summary>
        ///     Remove
        /// </summary>
        /// <param name="key">Key</param>
        /// <returns>Removed</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(in TKey key)
        {
            uint collisionCount = 0;
            var hashCode = (uint)key.GetHashCode();
            ref var bucket = ref GetBucket(hashCode);
            var last = -1;
            var i = bucket - 1;
            while (i >= 0)
            {
                ref var entry = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)i);
                if (entry.HashCode == hashCode && entry.Key.Equals(key))
                {
                    if (last < 0)
                        bucket = entry.Next + 1;
                    else
                        Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)last).Next = entry.Next;
                    entry.Next = -3 - _freeList;
                    _freeList = i;
                    _freeCount++;
                    return true;
                }

                last = i;
                i = entry.Next;
                collisionCount++;
                if (collisionCount > (uint)_entriesLength)
                    ThrowHelpers.ThrowConcurrentOperationsNotSupportedException();
            }

            return false;
        }

        /// <summary>
        ///     Remove
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        /// <returns>Removed</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(in TKey key, out TValue value)
        {
            uint collisionCount = 0;
            var hashCode = (uint)key.GetHashCode();
            ref var bucket = ref GetBucket(hashCode);
            var last = -1;
            var i = bucket - 1;
            while (i >= 0)
            {
                ref var entry = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)i);
                if (entry.HashCode == hashCode && entry.Key.Equals(key))
                {
                    if (last < 0)
                        bucket = entry.Next + 1;
                    else
                        Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)last).Next = entry.Next;
                    value = entry.Value;
                    entry.Next = -3 - _freeList;
                    _freeList = i;
                    _freeCount++;
                    return true;
                }

                last = i;
                i = entry.Next;
                collisionCount++;
                if (collisionCount > (uint)_entriesLength)
                    ThrowHelpers.ThrowConcurrentOperationsNotSupportedException();
            }

            value = default;
            return false;
        }

        /// <summary>
        ///     Contains key
        /// </summary>
        /// <param name="key">Key</param>
        /// <returns>Contains key</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool ContainsKey(in TKey key) => !Unsafe.IsNullRef(ref Unsafe.AsRef(in FindValue(key)));

        /// <summary>
        ///     Try to get the value
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        /// <returns>Got</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGetValue(in TKey key, out TValue value)
        {
            ref var valRef = ref FindValue(key);
            if (!Unsafe.IsNullRef(ref Unsafe.AsRef(in valRef)))
            {
                value = valRef;
                return true;
            }

            value = default;
            return false;
        }

        /// <summary>
        ///     Try to get the value
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        /// <returns>Got</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGetValueReference(in TKey key, out NativeReference<TValue> value)
        {
            ref var valRef = ref FindValue(key);
            if (!Unsafe.IsNullRef(ref Unsafe.AsRef(in valRef)))
            {
                value = new NativeReference<TValue>(Unsafe.AsPointer(ref valRef));
                return true;
            }

            value = default;
            return false;
        }

        /// <summary>
        ///     Get value ref
        /// </summary>
        /// <param name="key">Key</param>
        /// <returns>Value ref</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ref TValue GetValueRefOrNullRef(in TKey key) => ref FindValue(key);

        /// <summary>
        ///     Get value ref
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="exists">Exists</param>
        /// <returns>Value ref</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ref TValue GetValueRefOrNullRef(in TKey key, out bool exists)
        {
            ref var valRef = ref FindValue(key);
            exists = !Unsafe.IsNullRef(ref Unsafe.AsRef(in valRef));
            return ref valRef;
        }

        /// <summary>
        ///     Try to get value ref or add default
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        /// <returns>Got</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValueRefOrAddDefault(in TKey key, out NativeReference<TValue> value)
        {
            var hashCode = (uint)key.GetHashCode();
            uint collisionCount = 0;
            ref var bucket = ref GetBucket(hashCode);
            var i = bucket - 1;
            while (true)
            {
                if ((uint)i >= (uint)_entriesLength)
                    break;
                ref var entry = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)i);
                if (entry.HashCode == hashCode && entry.Key.Equals(key))
                {
                    value = new NativeReference<TValue>(Unsafe.AsPointer(ref entry.Value));
                    return true;
                }

                i = entry.Next;
                collisionCount++;
                if (collisionCount > (uint)_entriesLength)
                    ThrowHelpers.ThrowConcurrentOperationsNotSupportedException();
            }

            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                _freeList = -3 - Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)_freeList).Next;
                _freeCount--;
            }
            else
            {
                var count = _count;
                if (count == _entriesLength)
                {
                    value = default;
                    return false;
                }

                index = count;
                _count = count + 1;
            }

            ref var newEntry = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index);
            newEntry.HashCode = hashCode;
            newEntry.Next = bucket - 1;
            newEntry.Key = key;
            newEntry.Value = default;
            bucket = index + 1;
            _version++;
            value = new NativeReference<TValue>(Unsafe.AsPointer(ref newEntry.Value));
            return true;
        }

        /// <summary>
        ///     Try to get value ref or add default
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        /// <param name="exists">Exists</param>
        /// <returns>Got</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValueRefOrAddDefault(in TKey key, out NativeReference<TValue> value, out bool exists)
        {
            var hashCode = (uint)key.GetHashCode();
            uint collisionCount = 0;
            ref var bucket = ref GetBucket(hashCode);
            var i = bucket - 1;
            while (true)
            {
                if ((uint)i >= (uint)_entriesLength)
                    break;
                ref var entry = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)i);
                if (entry.HashCode == hashCode && entry.Key.Equals(key))
                {
                    value = new NativeReference<TValue>(Unsafe.AsPointer(ref entry.Value));
                    exists = true;
                    return true;
                }

                i = entry.Next;
                collisionCount++;
                if (collisionCount > (uint)_entriesLength)
                    ThrowHelpers.ThrowConcurrentOperationsNotSupportedException();
            }

            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                _freeList = -3 - Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)_freeList).Next;
                _freeCount--;
            }
            else
            {
                var count = _count;
                if (count == _entriesLength)
                {
                    value = default;
                    exists = false;
                    return false;
                }

                index = count;
                _count = count + 1;
            }

            ref var newEntry = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index);
            newEntry.HashCode = hashCode;
            newEntry.Next = bucket - 1;
            newEntry.Key = key;
            newEntry.Value = default;
            bucket = index + 1;
            _version++;
            value = new NativeReference<TValue>(Unsafe.AsPointer(ref newEntry.Value));
            exists = false;
            return true;
        }

        /// <summary>
        ///     Find value
        /// </summary>
        /// <param name="key">Key</param>
        /// <returns>Value</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly ref TValue FindValue(in TKey key)
        {
            var hashCode = (uint)key.GetHashCode();
            var i = GetBucket(hashCode);
            uint collisionCount = 0;
            i--;
            do
            {
                if ((uint)i >= (uint)_entriesLength)
                    return ref Unsafe.NullRef<TValue>();
                ref var entry = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)i);
                if (entry.HashCode == hashCode && entry.Key.Equals(key))
                    return ref entry.Value;
                i = entry.Next;
                collisionCount++;
            } while (collisionCount <= (uint)_entriesLength);

            ThrowHelpers.ThrowConcurrentOperationsNotSupportedException();
            return ref Unsafe.NullRef<TValue>();
        }

        /// <summary>
        ///     Insert
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private InsertResult TryInsertOverwriteExisting(in TKey key, in TValue value)
        {
            var hashCode = (uint)key.GetHashCode();
            uint collisionCount = 0;
            ref var bucket = ref GetBucket(hashCode);
            var i = bucket - 1;
            while (true)
            {
                if ((uint)i >= (uint)_entriesLength)
                    break;
                if (Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)i).HashCode == hashCode && Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)i).Key.Equals(key))
                {
                    Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)i).Value = value;
                    return InsertResult.Overwritten;
                }

                i = Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)i).Next;
                collisionCount++;
                if (collisionCount > (uint)_entriesLength)
                    ThrowHelpers.ThrowConcurrentOperationsNotSupportedException();
            }

            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                _freeList = -3 - Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)_freeList).Next;
                _freeCount--;
            }
            else
            {
                var count = _count;
                if (count == _entriesLength)
                    return InsertResult.InsufficientCapacity;
                index = count;
                _count = count + 1;
            }

            ref var entry = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index);
            entry.HashCode = hashCode;
            entry.Next = bucket - 1;
            entry.Key = key;
            entry.Value = value;
            bucket = index + 1;
            _version++;
            return InsertResult.Success;
        }

        /// <summary>
        ///     Insert
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private InsertResult TryInsertNone(in TKey key, in TValue value)
        {
            var hashCode = (uint)key.GetHashCode();
            uint collisionCount = 0;
            ref var bucket = ref GetBucket(hashCode);
            var i = bucket - 1;
            while (true)
            {
                if ((uint)i >= (uint)_entriesLength)
                    break;
                if (Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)i).HashCode == hashCode && Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)i).Key.Equals(key))
                    return InsertResult.AlreadyExists;
                i = Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)i).Next;
                collisionCount++;
                if (collisionCount > (uint)_entriesLength)
                    ThrowHelpers.ThrowConcurrentOperationsNotSupportedException();
            }

            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                _freeList = -3 - Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)_freeList).Next;
                _freeCount--;
            }
            else
            {
                var count = _count;
                if (count == _entriesLength)
                    return InsertResult.InsufficientCapacity;
                index = count;
                _count = count + 1;
            }

            ref var entry = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index);
            entry.HashCode = hashCode;
            entry.Next = bucket - 1;
            entry.Key = key;
            entry.Value = value;
            bucket = index + 1;
            _version++;
            return InsertResult.Success;
        }

        /// <summary>
        ///     Get bucket ref
        /// </summary>
        /// <param name="hashCode">HashCode</param>
        /// <returns>Bucket ref</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly ref int GetBucket(uint hashCode) => ref sizeof(nint) == 8 ? ref Unsafe.Add(ref Unsafe.AsRef<int>(_buckets), (nint)HashHelpers.FastMod(hashCode, (uint)_bucketsLength, _fastModMultiplier)) : ref Unsafe.Add(ref Unsafe.AsRef<int>(_buckets), (nint)(hashCode % _bucketsLength));

        /// <summary>
        ///     Entry
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct Entry
        {
            /// <summary>
            ///     HashCode
            /// </summary>
            public uint HashCode;

            /// <summary>
            ///     Next
            /// </summary>
            public int Next;

            /// <summary>
            ///     Key
            /// </summary>
            public TKey Key;

            /// <summary>
            ///     Value
            /// </summary>
            public TValue Value;
        }

        /// <summary>
        ///     Copy to
        /// </summary>
        /// <param name="buffer">Buffer</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void CopyTo(Span<KeyValuePair<TKey, TValue>> buffer)
        {
            ThrowHelpers.ThrowIfLessThan(buffer.Length, Count, nameof(buffer));
            ref var reference = ref MemoryMarshal.GetReference(buffer);
            var count = _count - _freeCount;
            var entries = _entries;
            var offset = 0;
            for (var index = 0; index < _count && count != 0; ++index)
            {
                ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)index);
                if (local.Next >= -1)
                {
                    Unsafe.WriteUnaligned(ref Unsafe.As<KeyValuePair<TKey, TValue>, byte>(ref Unsafe.Add(ref reference, (nint)offset++)), new KeyValuePair<TKey, TValue>(local.Key, local.Value));
                    --count;
                }
            }
        }

        /// <summary>
        ///     Copy to
        /// </summary>
        /// <param name="buffer">Buffer</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void CopyTo(Span<byte> buffer) => CopyTo(MemoryMarshal.Cast<byte, KeyValuePair<TKey, TValue>>(buffer));

        /// <summary>
        ///     Empty
        /// </summary>
        public static StackallocDictionary<TKey, TValue> Empty => new();

        /// <summary>
        ///     Get enumerator
        /// </summary>
        /// <returns>Enumerator</returns>
        public Enumerator GetEnumerator() => new(Unsafe.AsPointer(ref this));

        /// <summary>
        ///     Get enumerator
        /// </summary>
        readonly IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
        {
            ThrowHelpers.ThrowCannotCallGetEnumeratorException();
            return default;
        }

        /// <summary>
        ///     Get enumerator
        /// </summary>
        readonly IEnumerator IEnumerable.GetEnumerator()
        {
            ThrowHelpers.ThrowCannotCallGetEnumeratorException();
            return default;
        }

        /// <summary>
        ///     Enumerator
        /// </summary>
        public struct Enumerator
        {
            /// <summary>
            ///     NativeDictionary
            /// </summary>
            private readonly StackallocDictionary<TKey, TValue>* _nativeDictionary;

            /// <summary>
            ///     Version
            /// </summary>
            private readonly int _version;

            /// <summary>
            ///     Index
            /// </summary>
            private int _index;

            /// <summary>
            ///     Current
            /// </summary>
            private KeyValuePair<TKey, TValue> _current;

            /// <summary>
            ///     Structure
            /// </summary>
            /// <param name="nativeDictionary">NativeDictionary</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Enumerator(void* nativeDictionary)
            {
                var handle = (StackallocDictionary<TKey, TValue>*)nativeDictionary;
                _nativeDictionary = handle;
                _version = handle->_version;
                _index = 0;
                _current = default;
            }

            /// <summary>
            ///     Move next
            /// </summary>
            /// <returns>Moved</returns>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                var handle = _nativeDictionary;
                ThrowHelpers.ThrowIfEnumFailedVersion(_version, handle->_version);
                while ((uint)_index < (uint)handle->_count)
                {
                    ref var entry = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(handle->_entries), (nint)_index++);
                    if (entry.Next >= -1)
                    {
                        _current = new KeyValuePair<TKey, TValue>(entry.Key, entry.Value);
                        return true;
                    }
                }

                _index = handle->_count + 1;
                _current = default;
                return false;
            }

            /// <summary>
            ///     Current
            /// </summary>
            public readonly KeyValuePair<TKey, TValue> Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _current;
            }
        }

        /// <summary>
        ///     Key collection
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public readonly struct KeyCollection : IReadOnlyCollection<TKey>
        {
            /// <summary>
            ///     NativeDictionary
            /// </summary>
            private readonly StackallocDictionary<TKey, TValue>* _nativeDictionary;

            /// <summary>
            ///     Count
            /// </summary>
            public int Count => _nativeDictionary->Count;

            /// <summary>
            ///     Structure
            /// </summary>
            /// <param name="nativeDictionary">NativeDictionary</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal KeyCollection(void* nativeDictionary) => _nativeDictionary = (StackallocDictionary<TKey, TValue>*)nativeDictionary;

            /// <summary>
            ///     Copy to
            /// </summary>
            /// <param name="buffer">Buffer</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void CopyTo(Span<TKey> buffer)
            {
                ThrowHelpers.ThrowIfLessThan(buffer.Length, Count, nameof(buffer));
                ref var reference = ref MemoryMarshal.GetReference(buffer);
                var count = _nativeDictionary->_count - _nativeDictionary->_freeCount;
                var entries = _nativeDictionary->_entries;
                var offset = 0;
                for (var index = 0; index < _nativeDictionary->_count && count != 0; ++index)
                {
                    ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)index);
                    if (local.Next >= -1)
                    {
                        Unsafe.WriteUnaligned(ref Unsafe.As<TKey, byte>(ref Unsafe.Add(ref reference, (nint)offset++)), local.Key);
                        --count;
                    }
                }
            }

            /// <summary>
            ///     Copy to
            /// </summary>
            /// <param name="buffer">Buffer</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void CopyTo(Span<byte> buffer) => CopyTo(MemoryMarshal.Cast<byte, TKey>(buffer));

            /// <summary>
            ///     Get enumerator
            /// </summary>
            /// <returns>Enumerator</returns>
            public Enumerator GetEnumerator() => new(_nativeDictionary);

            /// <summary>
            ///     Get enumerator
            /// </summary>
            IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator()
            {
                ThrowHelpers.ThrowCannotCallGetEnumeratorException();
                return default;
            }

            /// <summary>
            ///     Get enumerator
            /// </summary>
            IEnumerator IEnumerable.GetEnumerator()
            {
                ThrowHelpers.ThrowCannotCallGetEnumeratorException();
                return default;
            }

            /// <summary>
            ///     Enumerator
            /// </summary>
            public struct Enumerator
            {
                /// <summary>
                ///     NativeDictionary
                /// </summary>
                private readonly StackallocDictionary<TKey, TValue>* _nativeDictionary;

                /// <summary>
                ///     Index
                /// </summary>
                private int _index;

                /// <summary>
                ///     Version
                /// </summary>
                private readonly int _version;

                /// <summary>
                ///     Current
                /// </summary>
                private TKey _currentKey;

                /// <summary>
                ///     Structure
                /// </summary>
                /// <param name="nativeDictionary">NativeDictionary</param>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal Enumerator(void* nativeDictionary)
                {
                    var handle = (StackallocDictionary<TKey, TValue>*)nativeDictionary;
                    _nativeDictionary = handle;
                    _version = handle->_version;
                    _index = 0;
                    _currentKey = default;
                }

                /// <summary>
                ///     Move next
                /// </summary>
                /// <returns>Moved</returns>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public bool MoveNext()
                {
                    var handle = _nativeDictionary;
                    ThrowHelpers.ThrowIfEnumFailedVersion(_version, handle->_version);
                    while ((uint)_index < (uint)handle->_count)
                    {
                        ref var entry = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(handle->_entries), (nint)_index++);
                        if (entry.Next >= -1)
                        {
                            _currentKey = entry.Key;
                            return true;
                        }
                    }

                    _index = handle->_count + 1;
                    _currentKey = default;
                    return false;
                }

                /// <summary>
                ///     Current
                /// </summary>
                public readonly TKey Current
                {
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    get => _currentKey;
                }
            }
        }

        /// <summary>
        ///     Value collection
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public readonly struct ValueCollection : IReadOnlyCollection<TValue>
        {
            /// <summary>
            ///     NativeDictionary
            /// </summary>
            private readonly StackallocDictionary<TKey, TValue>* _nativeDictionary;

            /// <summary>
            ///     Count
            /// </summary>
            public int Count => _nativeDictionary->Count;

            /// <summary>
            ///     Structure
            /// </summary>
            /// <param name="nativeDictionary">NativeDictionary</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal ValueCollection(void* nativeDictionary) => _nativeDictionary = (StackallocDictionary<TKey, TValue>*)nativeDictionary;

            /// <summary>
            ///     Copy to
            /// </summary>
            /// <param name="buffer">Buffer</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void CopyTo(Span<TValue> buffer)
            {
                ThrowHelpers.ThrowIfLessThan(buffer.Length, Count, nameof(buffer));
                ref var reference = ref MemoryMarshal.GetReference(buffer);
                var count = _nativeDictionary->_count - _nativeDictionary->_freeCount;
                var entries = _nativeDictionary->_entries;
                var offset = 0;
                for (var index = 0; index < _nativeDictionary->_count && count != 0; ++index)
                {
                    ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)index);
                    if (local.Next >= -1)
                    {
                        Unsafe.WriteUnaligned(ref Unsafe.As<TValue, byte>(ref Unsafe.Add(ref reference, (nint)offset++)), local.Value);
                        --count;
                    }
                }
            }

            /// <summary>
            ///     Copy to
            /// </summary>
            /// <param name="buffer">Buffer</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void CopyTo(Span<byte> buffer) => CopyTo(MemoryMarshal.Cast<byte, TValue>(buffer));

            /// <summary>
            ///     Get enumerator
            /// </summary>
            /// <returns>Enumerator</returns>
            public Enumerator GetEnumerator() => new(_nativeDictionary);

            /// <summary>
            ///     Get enumerator
            /// </summary>
            IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator()
            {
                ThrowHelpers.ThrowCannotCallGetEnumeratorException();
                return default;
            }

            /// <summary>
            ///     Get enumerator
            /// </summary>
            IEnumerator IEnumerable.GetEnumerator()
            {
                ThrowHelpers.ThrowCannotCallGetEnumeratorException();
                return default;
            }

            /// <summary>
            ///     Enumerator
            /// </summary>
            public struct Enumerator
            {
                /// <summary>
                ///     NativeDictionary
                /// </summary>
                private readonly StackallocDictionary<TKey, TValue>* _nativeDictionary;

                /// <summary>
                ///     Index
                /// </summary>
                private int _index;

                /// <summary>
                ///     Version
                /// </summary>
                private readonly int _version;

                /// <summary>
                ///     Current
                /// </summary>
                private TValue _currentValue;

                /// <summary>
                ///     Structure
                /// </summary>
                /// <param name="nativeDictionary">NativeDictionary</param>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal Enumerator(void* nativeDictionary)
                {
                    var handle = (StackallocDictionary<TKey, TValue>*)nativeDictionary;
                    _nativeDictionary = handle;
                    _version = handle->_version;
                    _index = 0;
                    _currentValue = default;
                }

                /// <summary>
                ///     Move next
                /// </summary>
                /// <returns>Moved</returns>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public bool MoveNext()
                {
                    var handle = _nativeDictionary;
                    ThrowHelpers.ThrowIfEnumFailedVersion(_version, handle->_version);
                    while ((uint)_index < (uint)handle->_count)
                    {
                        ref var entry = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(handle->_entries), (nint)_index++);
                        if (entry.Next >= -1)
                        {
                            _currentValue = entry.Value;
                            return true;
                        }
                    }

                    _index = handle->_count + 1;
                    _currentValue = default;
                    return false;
                }

                /// <summary>
                ///     Current
                /// </summary>
                public readonly TValue Current
                {
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    get => _currentValue;
                }
            }
        }
    }
}
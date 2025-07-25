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
    ///     Stackalloc ordered dictionary
    /// </summary>
    /// <typeparam name="TKey">Type</typeparam>
    /// <typeparam name="TValue">Type</typeparam>
    [StructLayout(LayoutKind.Sequential)]
    [StackallocCollection(FromType.Standard)]
    public unsafe struct StackallocOrderedDictionary<TKey, TValue> : IReadOnlyCollection<KeyValuePair<TKey, TValue>> where TKey : unmanaged, IEquatable<TKey> where TValue : unmanaged
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
        ///     Count
        /// </summary>
        private int _count;

        /// <summary>
        ///     Version
        /// </summary>
        private int _version;

        /// <summary>
        ///     FastModMultiplier
        /// </summary>
        private ulong _fastModMultiplier;

        /// <summary>
        ///     Is empty
        /// </summary>
        public readonly bool IsEmpty => _count == 0;

        /// <summary>
        ///     Count
        /// </summary>
        public readonly int Count => _count;

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
        public StackallocOrderedDictionary(Span<byte> buffer, int capacity)
        {
            var size = HashHelpers.GetPrime(capacity);
            var alignment = (uint)Math.Max(NativeMemoryAllocator.AlignOf<int>(), NativeMemoryAllocator.AlignOf<Entry>());
            var bucketsByteCount = (uint)NativeMemoryAllocator.AlignUp((nuint)(size * sizeof(int)), alignment);
            _buckets = (int*)NativeArray<byte>.Create(buffer, alignment).Buffer;
            _entries = UnsafeHelpers.AddByteOffset<Entry>(_buckets, (nint)bucketsByteCount);
            _bucketsLength = size;
            _entriesLength = size;
            _fastModMultiplier = sizeof(nint) == 8 ? HashHelpers.GetFastModMultiplier((uint)size) : 0;
            _count = 0;
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
                ++_version;
            }
        }

        /// <summary>
        ///     Try add
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public InsertResult TryAdd(in TKey key, in TValue value) => TryInsertIgnoreInsertion(-1, key, value);

        /// <summary>
        ///     Try insert
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public InsertResult TryInsert(in TKey key, in TValue value) => TryInsertOverwriteExisting(-1, key, value);

        /// <summary>
        ///     Remove
        /// </summary>
        /// <param name="key">Key</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(in TKey key)
        {
            var index = IndexOf(key);
            if (index >= 0)
            {
                var count = _count;
                RemoveEntryFromBucket(index);
                var entries = _entries;
                for (var entryIndex = index + 1; entryIndex < count; ++entryIndex)
                {
                    Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)(entryIndex - 1)) = Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)entryIndex);
                    UpdateBucketIndex(entryIndex, -1);
                }

                Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)(--_count)) = new Entry();
                ++_version;
                return true;
            }

            return false;
        }

        /// <summary>
        ///     Remove
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(in TKey key, out TValue value)
        {
            var index = IndexOf(key);
            if (index >= 0)
            {
                value = Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index).Value;
                var count = _count;
                RemoveEntryFromBucket(index);
                var entries = _entries;
                for (var entryIndex = index + 1; entryIndex < count; ++entryIndex)
                {
                    Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)(entryIndex - 1)) = Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)entryIndex);
                    UpdateBucketIndex(entryIndex, -1);
                }

                Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)(--_count)) = new Entry();
                ++_version;
                return true;
            }

            value = default;
            return false;
        }

        /// <summary>
        ///     Remove at
        /// </summary>
        /// <param name="index">Index</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveAt(int index)
        {
            var count = _count;
            ThrowHelpers.ThrowIfGreaterThanOrEqual((uint)index, (uint)count, nameof(index));
            RemoveEntryFromBucket(index);
            var entries = _entries;
            for (var entryIndex = index + 1; entryIndex < count; ++entryIndex)
            {
                Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)(entryIndex - 1)) = Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)entryIndex);
                UpdateBucketIndex(entryIndex, -1);
            }

            Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)(--_count)) = new Entry();
            ++_version;
        }

        /// <summary>
        ///     Remove at
        /// </summary>
        /// <param name="index">Index</param>
        /// <param name="keyValuePair">Key value pair</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveAt(int index, out KeyValuePair<TKey, TValue> keyValuePair)
        {
            var count = _count;
            ThrowHelpers.ThrowIfGreaterThanOrEqual((uint)index, (uint)count, nameof(index));
            ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index);
            keyValuePair = new KeyValuePair<TKey, TValue>(local.Key, local.Value);
            RemoveEntryFromBucket(index);
            var entries = _entries;
            for (var entryIndex = index + 1; entryIndex < count; ++entryIndex)
            {
                Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)(entryIndex - 1)) = Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)entryIndex);
                UpdateBucketIndex(entryIndex, -1);
            }

            Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)(--_count)) = new Entry();
            ++_version;
        }

        /// <summary>
        ///     Remove at
        /// </summary>
        /// <param name="index">Index</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRemoveAt(int index)
        {
            var count = _count;
            if ((uint)index >= (uint)count)
                return false;
            RemoveEntryFromBucket(index);
            var entries = _entries;
            for (var entryIndex = index + 1; entryIndex < count; ++entryIndex)
            {
                Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)(entryIndex - 1)) = Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)entryIndex);
                UpdateBucketIndex(entryIndex, -1);
            }

            Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)(--_count)) = new Entry();
            ++_version;
            return true;
        }

        /// <summary>
        ///     Remove at
        /// </summary>
        /// <param name="index">Index</param>
        /// <param name="keyValuePair">Key value pair</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRemoveAt(int index, out KeyValuePair<TKey, TValue> keyValuePair)
        {
            var count = _count;
            if ((uint)index >= (uint)count)
            {
                keyValuePair = default;
                return false;
            }

            ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index);
            keyValuePair = new KeyValuePair<TKey, TValue>(local.Key, local.Value);
            RemoveEntryFromBucket(index);
            var entries = _entries;
            for (var entryIndex = index + 1; entryIndex < count; ++entryIndex)
            {
                Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)(entryIndex - 1)) = Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)entryIndex);
                UpdateBucketIndex(entryIndex, -1);
            }

            Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)(--_count)) = new Entry();
            ++_version;
            return true;
        }

        /// <summary>
        ///     Contains key
        /// </summary>
        /// <param name="key">Key</param>
        /// <returns>Contains key</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool ContainsKey(in TKey key) => IndexOf(key) >= 0;

        /// <summary>
        ///     Try to get the value
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        /// <returns>Got</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGetValue(in TKey key, out TValue value)
        {
            var index = IndexOf(key);
            if (index >= 0)
            {
                value = Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index).Value;
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
            var index = IndexOf(key);
            if (index >= 0)
            {
                value = new NativeReference<TValue>(Unsafe.AsPointer(ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index).Value));
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
        public readonly ref TValue GetValueRefOrNullRef(in TKey key)
        {
            var index = IndexOf(key);
            return ref index >= 0 ? ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index).Value : ref Unsafe.NullRef<TValue>();
        }

        /// <summary>
        ///     Get value ref
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="exists">Exists</param>
        /// <returns>Value ref</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ref TValue GetValueRefOrNullRef(in TKey key, out bool exists)
        {
            var index = IndexOf(key);
            if (index >= 0)
            {
                exists = true;
                return ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index).Value;
            }

            exists = false;
            return ref Unsafe.NullRef<TValue>();
        }

        /// <summary>
        ///     Get value ref or add default
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        /// <returns>Value ref</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValueRefOrAddDefault(in TKey key, out NativeReference<TValue> value)
        {
            uint outHashCode = 0;
            uint outCollisionCount = 0;
            var index1 = IndexOf(key, ref outHashCode, ref outCollisionCount);
            if (index1 >= 0)
            {
                value = new NativeReference<TValue>(Unsafe.AsPointer(ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index1).Value));
                return true;
            }

            var index = _count;
            var entries = _entries;
            if (_entriesLength == _count)
            {
                value = default;
                return false;
            }

            for (var entryIndex = _count - 1; entryIndex >= index; --entryIndex)
            {
                Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)(entryIndex + 1)) = Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)entryIndex);
                UpdateBucketIndex(entryIndex, 1);
            }

            ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)index);
            local.HashCode = outHashCode;
            local.Key = key;
            local.Value = default;
            PushEntryIntoBucket(ref local, index);
            ++_count;
            ++_version;
            value = new NativeReference<TValue>(Unsafe.AsPointer(ref local.Value));
            return true;
        }

        /// <summary>
        ///     Get value ref or add default
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        /// <param name="exists">Exists</param>
        /// <returns>Value ref</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValueRefOrAddDefault(in TKey key, out NativeReference<TValue> value, out bool exists)
        {
            uint outHashCode = 0;
            uint outCollisionCount = 0;
            var index1 = IndexOf(key, ref outHashCode, ref outCollisionCount);
            if (index1 >= 0)
            {
                value = new NativeReference<TValue>(Unsafe.AsPointer(ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index1).Value));
                exists = true;
                return true;
            }

            var index = _count;
            var entries = _entries;
            if (_entriesLength == _count)
            {
                value = default;
                exists = false;
                return false;
            }

            for (var entryIndex = _count - 1; entryIndex >= index; --entryIndex)
            {
                Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)(entryIndex + 1)) = Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)entryIndex);
                UpdateBucketIndex(entryIndex, 1);
            }

            ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)index);
            local.HashCode = outHashCode;
            local.Key = key;
            local.Value = default;
            PushEntryIntoBucket(ref local, index);
            ++_count;
            ++_version;
            value = new NativeReference<TValue>(Unsafe.AsPointer(ref local.Value));
            exists = false;
            return true;
        }

        /// <summary>
        ///     Get key at index
        /// </summary>
        /// <param name="index">Index</param>
        /// <returns>Key</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly TKey GetKeyAt(int index)
        {
            ThrowHelpers.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count, nameof(index));
            ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index);
            return local.Key;
        }

        /// <summary>
        ///     Get value at index
        /// </summary>
        /// <param name="index">Index</param>
        /// <returns>Value</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly ref TValue GetValueAt(int index)
        {
            ThrowHelpers.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count, nameof(index));
            ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index);
            return ref local.Value;
        }

        /// <summary>
        ///     Get key at index
        /// </summary>
        /// <param name="index">Index</param>
        /// <param name="key">Key</param>
        /// <returns>Key</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGetKeyAt(int index, out TKey key)
        {
            if ((uint)index >= (uint)_count)
            {
                key = default;
                return false;
            }

            ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index);
            key = local.Key;
            return true;
        }

        /// <summary>
        ///     Get value at index
        /// </summary>
        /// <param name="index">Index</param>
        /// <param name="value">Value</param>
        /// <returns>Value</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGetValueAt(int index, out TValue value)
        {
            if ((uint)index >= (uint)_count)
            {
                value = default;
                return false;
            }

            ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index);
            value = local.Value;
            return true;
        }

        /// <summary>
        ///     Get value at index
        /// </summary>
        /// <param name="index">Index</param>
        /// <param name="value">Value</param>
        /// <returns>Value</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGetValueReferenceAt(int index, out NativeReference<TValue> value)
        {
            if ((uint)index >= (uint)_count)
            {
                value = default;
                return false;
            }

            ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index);
            value = new NativeReference<TValue>(Unsafe.AsPointer(ref local.Value));
            return true;
        }

        /// <summary>
        ///     Get at
        /// </summary>
        /// <param name="index">Index</param>
        /// <returns>KeyValuePair</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly KeyValuePair<TKey, TValue> GetAt(int index)
        {
            ThrowHelpers.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count, nameof(index));
            ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index);
            return new KeyValuePair<TKey, TValue>(local.Key, local.Value);
        }

        /// <summary>
        ///     Get at
        /// </summary>
        /// <param name="index">Index</param>
        /// <returns>KeyValuePair</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly KeyValuePair<TKey, NativeReference<TValue>> GetReferenceAt(int index)
        {
            ThrowHelpers.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count, nameof(index));
            ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index);
            return new KeyValuePair<TKey, NativeReference<TValue>>(local.Key, new NativeReference<TValue>(Unsafe.AsPointer(ref local.Value)));
        }

        /// <summary>
        ///     Get at
        /// </summary>
        /// <param name="index">Index</param>
        /// <param name="keyValuePair">KeyValuePair</param>
        /// <returns>Got</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGetAt(int index, out KeyValuePair<TKey, TValue> keyValuePair)
        {
            if ((uint)index >= (uint)_count)
            {
                keyValuePair = default;
                return false;
            }

            ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index);
            keyValuePair = new KeyValuePair<TKey, TValue>(local.Key, local.Value);
            return true;
        }

        /// <summary>
        ///     Get at
        /// </summary>
        /// <param name="index">Index</param>
        /// <param name="keyValuePair">KeyValuePair</param>
        /// <returns>Got</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGetReferenceAt(int index, out KeyValuePair<TKey, NativeReference<TValue>> keyValuePair)
        {
            if ((uint)index >= (uint)_count)
            {
                keyValuePair = default;
                return false;
            }

            ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index);
            keyValuePair = new KeyValuePair<TKey, NativeReference<TValue>>(local.Key, new NativeReference<TValue>(Unsafe.AsPointer(ref local.Value)));
            return true;
        }

        /// <summary>
        ///     Index of
        /// </summary>
        /// <param name="key">Key</param>
        /// <returns>Index</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int IndexOf(in TKey key)
        {
            uint num = 0;
            return IndexOf(key, ref num, ref num);
        }

        /// <summary>
        ///     Index of
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="outHashCode">Out hashCode</param>
        /// <param name="outCollisionCount">Out collision count</param>
        /// <returns>Index</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly int IndexOf(in TKey key, ref uint outHashCode, ref uint outCollisionCount)
        {
            uint num = 0;
            var entries = _entries;
            var hashCode = (uint)key.GetHashCode();
            var index = GetBucket(hashCode) - 1;
            while ((uint)index < (uint)_entriesLength)
            {
                ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)index);
                if ((int)local.HashCode != (int)hashCode || !local.Key.Equals(key))
                {
                    index = local.Next;
                    ++num;
                    if (num > (uint)_entriesLength)
                        ThrowHelpers.ThrowConcurrentOperationsNotSupportedException();
                }
                else
                {
                    outHashCode = hashCode;
                    return index;
                }
            }

            outCollisionCount = num;
            outHashCode = hashCode;
            return -1;
        }

        /// <summary>
        ///     Insert
        /// </summary>
        /// <param name="index">Index</param>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public InsertResult TryAdd(int index, in TKey key, in TValue value)
        {
            ThrowHelpers.ThrowIfGreaterThan((uint)index, (uint)_count, nameof(index));
            return TryInsertIgnoreInsertion(index, key, value);
        }

        /// <summary>
        ///     Insert
        /// </summary>
        /// <param name="index">Index</param>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public InsertResult TryInsert(int index, in TKey key, in TValue value)
        {
            ThrowHelpers.ThrowIfGreaterThan((uint)index, (uint)_count, nameof(index));
            return TryInsertOverwriteExisting(index, key, value);
        }

        /// <summary>
        ///     Set at
        /// </summary>
        /// <param name="index">Index</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void SetAt(int index, in TValue value)
        {
            ThrowHelpers.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count, nameof(index));
            Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index).Value = value;
        }

        /// <summary>
        ///     Set at
        /// </summary>
        /// <param name="index">Index</param>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetAt(int index, in TKey key, in TValue value)
        {
            ThrowHelpers.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count, nameof(index));
            ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index);
            if (key.Equals(local.Key))
            {
                local.Value = value;
                return;
            }

            uint outHashCode = 0;
            uint outCollisionCount = 0;
            if (IndexOf(key, ref outHashCode, ref outCollisionCount) >= 0)
                ThrowHelpers.ThrowAddingDuplicateWithKeyException(key);
            RemoveEntryFromBucket(index);
            local.HashCode = outHashCode;
            local.Key = key;
            local.Value = value;
            PushEntryIntoBucket(ref local, index);
            ++_version;
        }

        /// <summary>
        ///     Push entry into bucket
        /// </summary>
        /// <param name="entry">Entry</param>
        /// <param name="entryIndex">Entry index</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly void PushEntryIntoBucket(ref Entry entry, int entryIndex)
        {
            ref var local = ref GetBucket(entry.HashCode);
            entry.Next = local - 1;
            local = entryIndex + 1;
        }

        /// <summary>
        ///     Remove entry from bucket
        /// </summary>
        /// <param name="entryIndex">Entry index</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly void RemoveEntryFromBucket(int entryIndex)
        {
            var entries = _entries;
            var entry = Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)entryIndex);
            ref var local1 = ref GetBucket(entry.HashCode);
            if (local1 == entryIndex + 1)
            {
                local1 = entry.Next + 1;
            }
            else
            {
                var index = local1 - 1;
                var num = 0;
                while (true)
                {
                    do
                    {
                        ref var local2 = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)index);
                        if (local2.Next == entryIndex)
                        {
                            local2.Next = entry.Next;
                            return;
                        }

                        index = local2.Next;
                    } while (++num <= _entriesLength);

                    ThrowHelpers.ThrowConcurrentOperationsNotSupportedException();
                }
            }
        }

        /// <summary>
        ///     Update bucket index
        /// </summary>
        /// <param name="entryIndex">Entry index</param>
        /// <param name="shiftAmount">Shift amount</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly void UpdateBucketIndex(int entryIndex, int shiftAmount)
        {
            var entries = _entries;
            ref var local1 = ref GetBucket(Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)entryIndex).HashCode);
            if (local1 == entryIndex + 1)
            {
                local1 += shiftAmount;
            }
            else
            {
                var index = local1 - 1;
                var num = 0;
                while (true)
                {
                    do
                    {
                        ref var local2 = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)index);
                        if (local2.Next == entryIndex)
                        {
                            local2.Next += shiftAmount;
                            return;
                        }

                        index = local2.Next;
                    } while (++num <= _entriesLength);

                    ThrowHelpers.ThrowConcurrentOperationsNotSupportedException();
                }
            }
        }

        /// <summary>
        ///     Insert
        /// </summary>
        /// <param name="index">Index</param>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private InsertResult TryInsertIgnoreInsertion(int index, in TKey key, in TValue value)
        {
            uint outHashCode = 0;
            uint outCollisionCount = 0;
            var index1 = IndexOf(key, ref outHashCode, ref outCollisionCount);
            if (index1 >= 0)
                return InsertResult.AlreadyExists;
            if (index < 0)
                index = _count;
            var entries = _entries;
            if (_entriesLength == _count)
                return InsertResult.InsufficientCapacity;
            for (var entryIndex = _count - 1; entryIndex >= index; --entryIndex)
            {
                Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)(entryIndex + 1)) = Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)entryIndex);
                UpdateBucketIndex(entryIndex, 1);
            }

            ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)index);
            local.HashCode = outHashCode;
            local.Key = key;
            local.Value = value;
            PushEntryIntoBucket(ref local, index);
            ++_count;
            ++_version;
            return InsertResult.Success;
        }

        /// <summary>
        ///     Insert
        /// </summary>
        /// <param name="index">Index</param>
        /// <param name="key">Key</param>
        /// <param name="value">Value</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private InsertResult TryInsertOverwriteExisting(int index, in TKey key, in TValue value)
        {
            uint outHashCode = 0;
            uint outCollisionCount = 0;
            var index1 = IndexOf(key, ref outHashCode, ref outCollisionCount);
            if (index1 >= 0)
            {
                Unsafe.Add(ref Unsafe.AsRef<Entry>(_entries), (nint)index1).Value = value;
                return InsertResult.Overwritten;
            }

            if (index < 0)
                index = _count;
            var entries = _entries;
            if (_entriesLength == _count)
                return InsertResult.InsufficientCapacity;
            for (var entryIndex = _count - 1; entryIndex >= index; --entryIndex)
            {
                Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)(entryIndex + 1)) = Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)entryIndex);
                UpdateBucketIndex(entryIndex, 1);
            }

            ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)index);
            local.HashCode = outHashCode;
            local.Key = key;
            local.Value = value;
            PushEntryIntoBucket(ref local, index);
            ++_count;
            ++_version;
            return InsertResult.Success;
        }

        /// <summary>
        ///     Get bucket ref
        /// </summary>
        /// <param name="hashCode">HashCode</param>
        /// <returns>Bucket ref</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly ref int GetBucket(uint hashCode)
        {
            var buckets = _buckets;
            return ref sizeof(nint) == 8 ? ref Unsafe.Add(ref Unsafe.AsRef<int>(buckets), (nint)HashHelpers.FastMod(hashCode, (uint)_bucketsLength, _fastModMultiplier)) : ref Unsafe.Add(ref Unsafe.AsRef<int>(buckets), (nint)(hashCode % _bucketsLength));
        }

        /// <summary>
        ///     Entry
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct Entry
        {
            /// <summary>
            ///     Next
            /// </summary>
            public int Next;

            /// <summary>
            ///     HashCode
            /// </summary>
            public uint HashCode;

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
        /// <param name="count">Count</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int CopyTo(Span<KeyValuePair<TKey, TValue>> buffer, int count)
        {
            ThrowHelpers.ThrowIfNegative(count, nameof(count));
            ref var reference = ref MemoryMarshal.GetReference(buffer);
            count = Math.Min(buffer.Length, Math.Min(count, _count));
            var entries = _entries;
            for (var index = 0; index < count; ++index)
                Unsafe.WriteUnaligned(ref Unsafe.As<KeyValuePair<TKey, TValue>, byte>(ref Unsafe.Add(ref reference, index)), new KeyValuePair<TKey, TValue>(Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)index).Key, Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)index).Value));
            return count;
        }

        /// <summary>
        ///     Copy to
        /// </summary>
        /// <param name="buffer">Buffer</param>
        /// <param name="count">Count</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int CopyTo(Span<byte> buffer, int count) => CopyTo(MemoryMarshal.Cast<byte, KeyValuePair<TKey, TValue>>(buffer), count);

        /// <summary>
        ///     Copy to
        /// </summary>
        /// <param name="buffer">Buffer</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void CopyTo(Span<KeyValuePair<TKey, TValue>> buffer)
        {
            ThrowHelpers.ThrowIfLessThan(buffer.Length, Count, nameof(buffer));
            ref var reference = ref MemoryMarshal.GetReference(buffer);
            var entries = _entries;
            for (var index = 0; index < _count; ++index)
                Unsafe.WriteUnaligned(ref Unsafe.As<KeyValuePair<TKey, TValue>, byte>(ref Unsafe.Add(ref reference, (nint)index)), new KeyValuePair<TKey, TValue>(Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)index).Key, Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)index).Value));
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
        public static StackallocOrderedDictionary<TKey, TValue> Empty => new();

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
            ///     NativeOrderedDictionary
            /// </summary>
            private readonly StackallocOrderedDictionary<TKey, TValue>* _nativeOrderedDictionary;

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
            /// <param name="nativeOrderedDictionary">NativeOrderedDictionary</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Enumerator(void* nativeOrderedDictionary)
            {
                var handle = (StackallocOrderedDictionary<TKey, TValue>*)nativeOrderedDictionary;
                _nativeOrderedDictionary = handle;
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
                var handle = _nativeOrderedDictionary;
                ThrowHelpers.ThrowIfEnumFailedVersion(_version, handle->_version);
                if (_index < handle->_count)
                {
                    ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(handle->_entries), (nint)_index);
                    _current = new KeyValuePair<TKey, TValue>(local.Key, local.Value);
                    ++_index;
                    return true;
                }

                _current = new KeyValuePair<TKey, TValue>();
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
            ///     NativeOrderedDictionary
            /// </summary>
            private readonly StackallocOrderedDictionary<TKey, TValue>* _nativeOrderedDictionary;

            /// <summary>
            ///     Count
            /// </summary>
            public int Count => _nativeOrderedDictionary->Count;

            /// <summary>
            ///     Structure
            /// </summary>
            /// <param name="nativeOrderedDictionary">NativeOrderedDictionary</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal KeyCollection(void* nativeOrderedDictionary) => _nativeOrderedDictionary = (StackallocOrderedDictionary<TKey, TValue>*)nativeOrderedDictionary;

            /// <summary>
            ///     Copy to
            /// </summary>
            /// <param name="buffer">Buffer</param>
            /// <param name="count">Count</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int CopyTo(Span<TKey> buffer, int count)
            {
                ThrowHelpers.ThrowIfNegative(count, nameof(count));
                ref var reference = ref MemoryMarshal.GetReference(buffer);
                count = Math.Min(buffer.Length, Math.Min(count, _nativeOrderedDictionary->_count));
                var entries = _nativeOrderedDictionary->_entries;
                for (var index = 0; index < count; ++index)
                    Unsafe.WriteUnaligned(ref Unsafe.As<TKey, byte>(ref Unsafe.Add(ref reference, index)), Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)index).Key);
                return count;
            }

            /// <summary>
            ///     Copy to
            /// </summary>
            /// <param name="buffer">Buffer</param>
            /// <param name="count">Count</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int CopyTo(Span<byte> buffer, int count) => CopyTo(MemoryMarshal.Cast<byte, TKey>(buffer), count);

            /// <summary>
            ///     Copy to
            /// </summary>
            /// <param name="buffer">Buffer</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void CopyTo(Span<TKey> buffer)
            {
                ThrowHelpers.ThrowIfLessThan(buffer.Length, Count, nameof(buffer));
                ref var reference = ref MemoryMarshal.GetReference(buffer);
                var entries = _nativeOrderedDictionary->_entries;
                for (var index = 0; index < _nativeOrderedDictionary->_count; ++index)
                    Unsafe.WriteUnaligned(ref Unsafe.As<TKey, byte>(ref Unsafe.Add(ref reference, (nint)index)), Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)index).Key);
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
            public Enumerator GetEnumerator() => new(_nativeOrderedDictionary);

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
                ///     NativeOrderedDictionary
                /// </summary>
                private readonly StackallocOrderedDictionary<TKey, TValue>* _nativeOrderedDictionary;

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
                /// <param name="nativeOrderedDictionary">NativeOrderedDictionary</param>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal Enumerator(void* nativeOrderedDictionary)
                {
                    var handle = (StackallocOrderedDictionary<TKey, TValue>*)nativeOrderedDictionary;
                    _nativeOrderedDictionary = handle;
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
                    var handle = _nativeOrderedDictionary;
                    ThrowHelpers.ThrowIfEnumFailedVersion(_version, handle->_version);
                    if (_index < handle->_count)
                    {
                        ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(handle->_entries), (nint)_index);
                        _currentKey = local.Key;
                        ++_index;
                        return true;
                    }

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
            ///     NativeOrderedDictionary
            /// </summary>
            private readonly StackallocOrderedDictionary<TKey, TValue>* _nativeOrderedDictionary;

            /// <summary>
            ///     Count
            /// </summary>
            public int Count => _nativeOrderedDictionary->Count;

            /// <summary>
            ///     Structure
            /// </summary>
            /// <param name="nativeOrderedDictionary">NativeOrderedDictionary</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal ValueCollection(void* nativeOrderedDictionary) => _nativeOrderedDictionary = (StackallocOrderedDictionary<TKey, TValue>*)nativeOrderedDictionary;

            /// <summary>
            ///     Copy to
            /// </summary>
            /// <param name="buffer">Buffer</param>
            /// <param name="count">Count</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int CopyTo(Span<TValue> buffer, int count)
            {
                ThrowHelpers.ThrowIfNegative(count, nameof(count));
                ref var reference = ref MemoryMarshal.GetReference(buffer);
                count = Math.Min(buffer.Length, Math.Min(count, _nativeOrderedDictionary->_count));
                var entries = _nativeOrderedDictionary->_entries;
                for (var index = 0; index < count; ++index)
                    Unsafe.WriteUnaligned(ref Unsafe.As<TValue, byte>(ref Unsafe.Add(ref reference, index)), Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)index).Value);
                return count;
            }

            /// <summary>
            ///     Copy to
            /// </summary>
            /// <param name="buffer">Buffer</param>
            /// <param name="count">Count</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int CopyTo(Span<byte> buffer, int count) => CopyTo(MemoryMarshal.Cast<byte, TValue>(buffer), count);

            /// <summary>
            ///     Copy to
            /// </summary>
            /// <param name="buffer">Buffer</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void CopyTo(Span<TValue> buffer)
            {
                ThrowHelpers.ThrowIfLessThan(buffer.Length, Count, nameof(buffer));
                ref var reference = ref MemoryMarshal.GetReference(buffer);
                var entries = _nativeOrderedDictionary->_entries;
                for (var index = 0; index < _nativeOrderedDictionary->_count; ++index)
                    Unsafe.WriteUnaligned(ref Unsafe.As<TValue, byte>(ref Unsafe.Add(ref reference, (nint)index)), Unsafe.Add(ref Unsafe.AsRef<Entry>(entries), (nint)index).Value);
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
            public Enumerator GetEnumerator() => new(_nativeOrderedDictionary);

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
                ///     NativeOrderedDictionary
                /// </summary>
                private readonly StackallocOrderedDictionary<TKey, TValue>* _nativeOrderedDictionary;

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
                /// <param name="nativeOrderedDictionary">NativeOrderedDictionary</param>
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal Enumerator(void* nativeOrderedDictionary)
                {
                    var handle = (StackallocOrderedDictionary<TKey, TValue>*)nativeOrderedDictionary;
                    _nativeOrderedDictionary = handle;
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
                    var handle = _nativeOrderedDictionary;
                    ThrowHelpers.ThrowIfEnumFailedVersion(_version, handle->_version);
                    if (_index < handle->_count)
                    {
                        ref var local = ref Unsafe.Add(ref Unsafe.AsRef<Entry>(handle->_entries), (nint)_index);
                        _currentValue = local.Value;
                        ++_index;
                        return true;
                    }

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
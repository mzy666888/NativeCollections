﻿using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#pragma warning disable CA2208
#pragma warning disable CS8632

// ReSharper disable ALL

namespace NativeCollections
{
    /// <summary>
    ///     Native split any range
    /// </summary>
    /// <typeparam name="T">Type</typeparam>
    [StructLayout(LayoutKind.Sequential)]
    [NativeCollection(FromType.None)]
    public readonly ref struct NativeSplitAnyRange<T> where T : IEquatable<T>
    {
        /// <summary>
        ///     Buffer
        /// </summary>
        private readonly ReadOnlySpan<T> _buffer;

        /// <summary>
        ///     Separator
        /// </summary>
        private readonly ReadOnlySpan<T> _separator;

        /// <summary>
        ///     Structure
        /// </summary>
        /// <param name="buffer">Buffer</param>
        /// <param name="separator">Separator</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeSplitAnyRange(ReadOnlySpan<T> buffer, in T separator)
        {
            if (Unsafe.IsNullRef(ref MemoryMarshal.GetReference(buffer)) || buffer.Length == 0)
                ThrowHelpers.ThrowArgumentNullException(nameof(buffer));
            if (!typeof(T).IsValueType && separator == null)
                ThrowHelpers.ThrowArgumentNullException(nameof(separator));
            _buffer = buffer;
            _separator = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in separator), 1);
        }

        /// <summary>
        ///     Structure
        /// </summary>
        /// <param name="buffer">Buffer</param>
        /// <param name="separator">Separator</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeSplitAnyRange(ReadOnlySpan<T> buffer, ReadOnlySpan<T> separator)
        {
            if (Unsafe.IsNullRef(ref MemoryMarshal.GetReference(buffer)) || buffer.Length == 0)
                ThrowHelpers.ThrowArgumentNullException(nameof(buffer));
            if (Unsafe.IsNullRef(ref MemoryMarshal.GetReference(separator)) || separator.Length == 0)
                ThrowHelpers.ThrowArgumentNullException(nameof(separator));
            _buffer = buffer;
            _separator = separator;
        }

        /// <summary>
        ///     Empty
        /// </summary>
        public static NativeSplitAnyRange<T> Empty => new();

        /// <summary>
        ///     Get enumerator
        /// </summary>
        public Enumerator GetEnumerator() => new(_buffer, _separator);

        /// <summary>
        ///     Enumerator
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public ref struct Enumerator
        {
            /// <summary>
            ///     Current
            /// </summary>
            private Range _current;

            /// <summary>
            ///     Next
            /// </summary>
            private int _next;

            /// <summary>
            ///     Buffer
            /// </summary>
            private readonly ReadOnlySpan<T> _buffer;

            /// <summary>
            ///     Separator
            /// </summary>
            private readonly ReadOnlySpan<T> _separator;

            /// <summary>
            ///     Structure
            /// </summary>
            /// <param name="buffer">Buffer</param>
            /// <param name="separator">Separator</param>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Enumerator(ReadOnlySpan<T> buffer, ReadOnlySpan<T> separator)
            {
                _current = default;
                _next = 0;
                _buffer = buffer;
                _separator = separator;
            }

            /// <summary>
            ///     Move next
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                var buffer = _buffer.Slice(_next);
                var index = _separator.Length == 1 ? buffer.IndexOf(_separator[0]) : buffer.IndexOfAny(_separator);
                if (index < 0)
                {
                    if (buffer.Length > 0)
                    {
                        _current = new Range(_next, _next + buffer.Length);
                        _next = _buffer.Length;
                        return true;
                    }

                    return false;
                }

                _current = new Range(_next, _next + index);
                _next += index + 1;
                return true;
            }

            /// <summary>
            ///     Current
            /// </summary>
            public readonly Range Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _current;
            }
        }
    }
}
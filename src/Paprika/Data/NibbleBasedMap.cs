﻿using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paprika.Crypto;
using Paprika.Store;
using Paprika.Utils;

namespace Paprika.Data;

/// <summary>
/// Represents an in-page map, responsible for storing items and information related to them.
/// Allows for efficient nibble enumeration so that if a subset of items should be extracted, it's easy to do so.
/// </summary>
/// <remarks>
/// The map is fixed in since as it's page dependent, hence the name.
/// It is a modified version of a slot array, that does not externalize slot indexes.
///
/// It keeps an internal map, now implemented with a not-the-best loop over slots.
/// With the use of key prefix, it should be small enough and fast enough for now.
/// </remarks>
public readonly ref struct NibbleBasedMap
{
    public const int MinSize = AllocationGranularity * 3;

    private const int AllocationGranularity = 8;

    private readonly ref Header _header;
    private readonly Span<byte> _data;
    private readonly Span<Slot> _slots;
    private readonly Span<byte> _raw;

    public NibbleBasedMap(Span<byte> buffer)
    {
        _raw = buffer;
        _header = ref Unsafe.As<byte, Header>(ref _raw[0]);
        _data = buffer.Slice(Header.Size);
        _slots = MemoryMarshal.Cast<byte, Slot>(_data);
    }

    public bool TrySet(in Key key, ReadOnlySpan<byte> data)
    {
        if (TryGetImpl(key, out var existingData, out var index))
        {
            // same size, copy in place
            if (data.Length == existingData.Length)
            {
                data.CopyTo(existingData);
                return true;
            }

            // cannot reuse, delete existing and add again
            DeleteImpl(index);
        }

        var hash = Slot.ExtractPrefix(key.Path, out var path);
        var encodedKey = path.WriteTo(stackalloc byte[path.MaxByteLength]);

        // does not exist yet, calculate total memory needed
        var total = GetTotalSpaceRequired(encodedKey, key.AdditionalKey, data);

        if (_header.Taken + total + Slot.Size > _data.Length)
        {
            if (_header.Deleted == 0)
            {
                // nothing to reclaim
                return false;
            }

            // there are some deleted entries, run defragmentation of the buffer and try again
            Deframent();

            // re-evaluate again
            if (_header.Taken + total + Slot.Size > _data.Length)
            {
                // not enough memory
                return false;
            }
        }

        var at = _header.Low;
        ref var slot = ref _slots[at / Slot.Size];

        // write slot
        slot.Prefix = hash;
        slot.ItemAddress = (ushort)(_data.Length - _header.High - total);
        slot.Type = key.Type;

        // write item: key, additionalKey, data
        var dest = _data.Slice(slot.ItemAddress, total);

        encodedKey.CopyTo(dest);
        key.AdditionalKey.CopyTo(dest.Slice(encodedKey.Length));
        data.CopyTo(dest.Slice(encodedKey.Length + key.AdditionalKey.Length));

        // commit low and high
        _header.Low += Slot.Size;
        _header.High += (ushort)total;
        return true;
    }

    /// <summary>
    /// Gets how many slots are used in the map.
    /// </summary>
    public int Count => _header.Low / Slot.Size;

    public int CapacityLeft => _data.Length - _header.Taken;

    public NibbleEnumerator EnumerateNibble(byte nibble) => new(this, nibble);

    public NibbleEnumerator EnumerateAll() => new(this, NibbleEnumerator.AllNibbles);

    public ref struct NibbleEnumerator
    {
        public const byte AllNibbles = byte.MaxValue;

        /// <summary>The map being enumerated.</summary>
        private NibbleBasedMap _map;

        /// <summary>
        /// The nibble being enumerated.
        /// </summary>
        private readonly byte _nibble;

        /// <summary>The next index to yield.</summary>
        private int _index;

        private readonly byte[] _bytes;
        private Item _current;

        internal NibbleEnumerator(NibbleBasedMap map, byte nibble)
        {
            _map = map;
            _nibble = nibble;
            _index = -1;
            _bytes = ArrayPool<byte>.Shared.Rent(128);
        }

        /// <summary>Advances the enumerator to the next element of the span.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            int index = _index + 1;
            var to = _map.Count;

            while (index < to &&
                   (_map._slots[index].Type == DataType.Deleted || // filter out deleted
                    (_nibble != AllNibbles &&
                     (
                         _map._slots[index].NibbleCount == 0 ||
                         _map._slots[index].FirstNibbleOfPrefix != _nibble))))
            {
                index += 1;
            }

            if (index < to)
            {
                _index = index;
                _current = Build();
                return true;
            }

            return false;
        }

        public Item Current => _current;

        private Item Build()
        {
            ref var slot = ref _map._slots[_index];
            var span = _map.GetSlotPayload(ref slot);

            ReadOnlySpan<byte> data;
            NibblePath path;

            // path rebuilding
            Span<byte> nibbles = stackalloc byte[3];
            var count = slot.DecodeNibblesFromPrefix(nibbles);

            if (count == 0)
            {
                // no nibbles stored in the slot, read as is.
                data = NibblePath.ReadFrom(span, out path);
            }
            else
            {
                // there's at least one nibble extracted
                var raw = NibblePath.RawExtract(span);
                data = span.Slice(raw.Length);

                const int space = 2;

                var bytes = _bytes.AsSpan(0, raw.Length + space); //big enough to handle all cases

                // copy forward enough to allow negative pointer arithmetics
                var pathDestination = bytes.Slice(space);
                raw.CopyTo(pathDestination);

                // Terribly unsafe region!
                // Operate on the copy, pathDestination, as it will be overwritten with unsafe ref.
                NibblePath.ReadFrom(pathDestination, out path);

                var countOdd = (byte)(count & 1);
                for (var i = 0; i < count; i++)
                {
                    path.UnsafeSetAt(i - count - 1, countOdd, nibbles[i]);
                }

                path = path.CopyWithUnsafePointerMoveBack(count);
            }

            if (slot.Type == DataType.StorageCell)
            {
                const int size = Keccak.Size;
                var additionalKey = data.Slice(0, size);
                return new Item(Key.StorageCell(path, additionalKey), data.Slice(size), _index, slot.Type);
            }

            return new Item(Key.Raw(path, slot.Type), data, _index, slot.Type);
        }

        public void Dispose()
        {
            if (_bytes != null)
                ArrayPool<byte>.Shared.Return(_bytes);
        }

        public readonly ref struct Item
        {
            public int Index { get; }
            public DataType Type { get; }
            public Key Key { get; }
            public ReadOnlySpan<byte> RawData { get; }

            public Item(Key key, ReadOnlySpan<byte> rawData, int index, DataType type)
            {
                Index = index;
                Type = type;
                Key = key;
                RawData = rawData;
            }
        }

        // a shortcut to not allocate, just copy the enumerator
        public NibbleEnumerator GetEnumerator() => this;
    }

    /// <summary>
    /// Gets the nibble representing the biggest bucket and provides stats to the caller.
    /// </summary>
    /// <returns>
    /// The nibble and how much of the page is occupied by storage cells that start their key with the nibble.
    /// </returns>
    public (byte nibble, double storageCellPercentageInPage) GetBiggestNibbleStats()
    {
        const int bucketCount = 16;

        Span<byte> storageCellCount = stackalloc byte[bucketCount];
        Span<byte> slotCount = stackalloc byte[bucketCount];

        var totalSlotCount = 0;

        var to = _header.Low / Slot.Size;
        for (var i = 0; i < to; i++)
        {
            ref readonly var slot = ref _slots[i];

            // extract only not deleted and these which have at least one nibble
            if (slot.Type != DataType.Deleted && slot.NibbleCount > 0)
            {
                var index = slot.FirstNibbleOfPrefix % bucketCount;

                if (slot.Type == DataType.StorageCell)
                {
                    storageCellCount[index]++;
                }

                slotCount[index]++;
                totalSlotCount++;
            }
        }

        var maxNibble = 0;

        for (int nibble = 1; nibble < bucketCount; nibble++)
        {
            var currentCount = slotCount[nibble];
            var maxCount = slotCount[maxNibble];
            if (currentCount > maxCount)
            {
                maxNibble = nibble;
            }
        }

        var storageCellPercentageInPage = (double)storageCellCount[maxNibble] / totalSlotCount;

        return ((byte)maxNibble, storageCellPercentageInPage);
    }

    private static int GetTotalSpaceRequired(ReadOnlySpan<byte> key, ReadOnlySpan<byte> additionalKey,
        ReadOnlySpan<byte> data)
    {
        return key.Length + data.Length + additionalKey.Length;
    }

    /// <summary>
    /// Warning! This does not set any tombstone so the reader won't be informed about a delete,
    /// just will miss the value.
    /// </summary>
    public bool Delete(in Key key)
    {
        if (TryGetImpl(key, out _, out var index))
        {
            DeleteImpl(index);
            return true;
        }

        return false;
    }

    public void Delete(in NibbleEnumerator.Item item) => DeleteImpl(item.Index);

    private void DeleteImpl(int index)
    {
        // mark as deleted first
        _slots[index].Type = DataType.Deleted;
        _header.Deleted++;

        // always try to compact after delete
        CollectTombstones();
    }

    private void Deframent()
    {
        // s as data were fitting before, the will fit after so all the checks can be skipped
        var size = _raw.Length;
        var array = ArrayPool<byte>.Shared.Rent(size);
        var span = array.AsSpan(0, size);

        span.Clear();
        var copy = new NibbleBasedMap(span);
        var count = _header.Low / Slot.Size;

        for (int i = 0; i < count; i++)
        {
            var copyFrom = _slots[i];
            if (copyFrom.Type != DataType.Deleted)
            {
                var fromSpan = GetSlotPayload(ref _slots[i]);

                ref var copyTo = ref copy._slots[copy._header.Low / Slot.Size];

                // copy raw, no decoding
                var high = (ushort)(copy._data.Length - copy._header.High - fromSpan.Length);
                fromSpan.CopyTo(copy._data.Slice(high));

                copyTo.Prefix = copyFrom.Prefix;
                copyTo.ItemAddress = high;
                copyTo.Type = copyFrom.Type;

                copy._header.Low += Slot.Size;
                copy._header.High = (ushort)(copy._header.High + fromSpan.Length);
            }
        }

        // finalize by coping over to this
        span.CopyTo(_raw);

        ArrayPool<byte>.Shared.Return(array);
        Debug.Assert(copy._header.Deleted == 0, "All deleted should be gone");
    }

    /// <summary>
    /// Collects tombstones of entities that used to be. 
    /// </summary>
    private void CollectTombstones()
    {
        // start with the last written and perform checks and cleanup till all the deleted are gone
        var index = Count - 1;

        while (index >= 0 && _slots[index].Type == DataType.Deleted)
        {
            // undo writing low
            _header.Low -= Slot.Size;

            // undo writing high
            var slice = GetSlotPayload(ref _slots[index]);
            var total = slice.Length;
            _header.High = (ushort)(_header.High - total);

            // cleanup
            _slots[index] = default;
            _header.Deleted--;

            // move back by one to see if it's deleted as well
            index--;
        }
    }

    public bool TryGet(in Key key, out ReadOnlySpan<byte> data)
    {
        if (TryGetImpl(key, out var span, out _))
        {
            data = span;
            return true;
        }

        data = default;
        return false;
    }

    [OptimizationOpportunity(OptimizationType.CPU,
        "key.Write to might be called twice, here and in TrySet")]
    private bool TryGetImpl(in Key key, out Span<byte> data, out int slotIndex)
    {
        var hash = Slot.ExtractPrefix(key.Path, out var path);
        var encodedKey = path.WriteTo(stackalloc byte[path.MaxByteLength]);

        var to = _header.Low / Slot.Size;

        // uses vectorized search, treating slots as a Span<ushort>
        // if the found index is odd -> found a slot to be queried

        const int notFound = -1;
        var span = MemoryMarshal.Cast<Slot, ushort>(_slots.Slice(0, to));

        var offset = 0;
        int index;

        while ((index = span.IndexOf(hash)) != notFound)
        {
            // move offset to the given position
            offset += index;

            if ((offset & Slot.PrefixUshortMask) == Slot.PrefixUshortMask)
            {
                var i = offset / 2;

                ref var slot = ref _slots[i];
                if (slot.Type == key.Type)
                {
                    var actual = GetSlotPayload(ref slot);

                    // The StartsWith check assumes that all the keys have the same length.
                    if (actual.StartsWith(encodedKey))
                    {
                        if (key.AdditionalKey.IsEmpty)
                        {
                            // no additional key, just assert encoded
                            data = actual.Slice(encodedKey.Length);
                            slotIndex = i;
                            return true;
                        }

                        // there's the additional key, assert it
                        // do it by slicing off first the encoded and then check the additional
                        if (actual.Slice(encodedKey.Length).StartsWith(key.AdditionalKey))
                        {
                            data = actual.Slice(encodedKey.Length + key.AdditionalKey.Length);
                            slotIndex = i;
                            return true;
                        }
                    }
                }
            }

            if (index + 1 >= span.Length)
            {
                // the span is empty and there's not place to move forward
                break;
            }

            // move next: ushorts sliced to the next
            // offset moved by 1 to align
            span = span.Slice(index + 1);
            offset += 1;
        }

        data = default;
        slotIndex = default;
        return false;
    }

    /// <summary>
    /// Gets the payload pointed to by the given slot without the length prefix.
    /// </summary>
    private Span<byte> GetSlotPayload(ref Slot slot)
    {
        // assert whether the slot has a previous, if not use data.length
        int previousSlotAddress = Unsafe.IsAddressLessThan(ref _slots[0], ref slot)
            ? Unsafe.Add(ref slot, -1).ItemAddress
            : _data.Length;

        return _data.Slice(slot.ItemAddress, previousSlotAddress - slot.ItemAddress);
    }

    [StructLayout(LayoutKind.Explicit, Size = Size)]
    public struct Slot
    {
        public const int Size = 4;

        // ItemAddress, requires 12 bits [0-11] to address whole page 
        private const ushort AddressMask = Page.PageSize - 1;

        /// <summary>
        /// The address of this item.
        /// </summary>
        public ushort ItemAddress
        {
            get => (ushort)(Raw & AddressMask);
            set => Raw = (ushort)((Raw & ~AddressMask) | value);
        }

        private const int DataTypeShift = 12;
        private const ushort DataTypeMask = unchecked((ushort)(1111 << DataTypeShift));

        /// <summary>
        /// The data type contained in this slot.
        /// </summary>
        public DataType Type
        {
            get => (DataType)((Raw & DataTypeMask) >> DataTypeShift);
            set => Raw = (ushort)((Raw & ~DataTypeMask) | (ushort)((byte)value << DataTypeShift));
        }

        [FieldOffset(0)] private ushort Raw;

        /// <summary>
        /// Used for vectorized search
        /// </summary>
        public const int PrefixUshortMask = 1;

        /// <summary>
        /// The memorized result of <see cref="ExtractPrefix"/> of this item.
        /// </summary>
        [FieldOffset(2)] public ushort Prefix;

        // encode length trimmed as highest nibble
        private const int NibbleCountShift = NibblePath.NibbleShift * 3;
        private const int Mask0 = (1 << Shift1) - 1;
        private const int Mask1 = (1 << Shift2) - 1 - Mask0;
        private const int Mask2 = (1 << NibblePath.NibbleShift * 3) - 1 - Mask1 - Mask0;
        private const int Shift0 = NibblePath.NibbleShift * 0;
        private const int Shift1 = NibblePath.NibbleShift * 1;
        private const int Shift2 = NibblePath.NibbleShift * 2;

        /// <summary>
        /// Builds the hash for the key.
        /// </summary>
        public static ushort ExtractPrefix(NibblePath key, out NibblePath result)
        {
            switch (key.Length)
            {
                case 0:
                    result = key;
                    return 0;
                case 1:
                    result = key.SliceFrom(1);
                    return (ushort)(
                        (1 << NibbleCountShift) +
                        (key.GetAt(0) << Shift0)
                    );
                case 2:
                    result = key.SliceFrom(2);
                    return (ushort)(
                        (2 << NibbleCountShift) +
                        (key.GetAt(0) << Shift0) +
                        (key.GetAt(1) << Shift1)
                    );
                default:
                    // 3 or more
                    result = key.SliceFrom(3);
                    return (ushort)(
                        (3 << NibbleCountShift) +
                        (key.GetAt(0) << Shift0) +
                        (key.GetAt(1) << Shift1) +
                        (key.GetAt(2) << Shift2)
                    );
            }
        }

        public int DecodeNibblesFromPrefix(Span<byte> nibbles)
        {
            var count = Prefix >> NibbleCountShift;
            switch (count)
            {
                case 0:
                    return 0;
                case 1:
                    nibbles[0] = (byte)(Prefix & Mask0);
                    return 1;
                case 2:
                    nibbles[0] = (byte)(Prefix & Mask0);
                    nibbles[1] = (byte)((Prefix & Mask1) >> Shift1);
                    return 2;
                default:
                    nibbles[0] = (byte)(Prefix & Mask0);
                    nibbles[1] = (byte)((Prefix & Mask1) >> Shift1);
                    nibbles[2] = (byte)((Prefix & Mask2) >> Shift2);
                    return 3;
            }
        }

        public byte FirstNibbleOfPrefix => (byte)(Prefix & Mask0);

        /// <summary>
        /// Gets the nibble count encoded in the prefix
        /// </summary>
        public int NibbleCount => Prefix >> NibbleCountShift;

        public override string ToString()
        {
            return
                $"{nameof(Type)}: {Type}, {nameof(Prefix)}: {Prefix}, {nameof(ItemAddress)}: {ItemAddress}";
        }
    }

    public override string ToString() =>
        $"{nameof(Count)}: {Count}, " +
        $"{nameof(CapacityLeft)}: {CapacityLeft}, " +
        $"Hash of content: {XxHash32.HashToUInt32(_raw)}";

    [StructLayout(LayoutKind.Explicit, Size = Size)]
    private struct Header
    {
        public const int Size = 8;

        /// <summary>
        /// Represents the distance from the start.
        /// </summary>
        [FieldOffset(0)] public ushort Low;

        /// <summary>
        /// Represents the distance from the end.
        /// </summary>
        [FieldOffset(2)] public ushort High;

        /// <summary>
        /// A rough estimates of gaps.
        /// </summary>
        [FieldOffset(4)] public ushort Deleted;

        public ushort Taken => (ushort)(Low + High);
    }
}
// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm;

using Word = Vector256<byte>;
using HalfWord = Vector128<byte>;

[StructLayout(LayoutKind.Auto)]
public ref struct EvmStack
{
    public const int RegisterLength = 1;
    public const int MaxStackSize = 1025;
    public const int ReturnStackSize = 1025;
    public const int WordSize = 32;
    public const int AddressSize = 20;

    public EvmStack(scoped in int head, ITxTracer txTracer, scoped in Span<byte> bytes)
    {
        Head = head;
        _tracer = txTracer;
        _bytes = bytes;
    }

    private readonly ITxTracer _tracer;
    private readonly Span<byte> _bytes;
    public int Head;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref byte PushBytesRef()
    {
        // Workhorse method
        int head = Head;
        if ((Head = head + 1) >= MaxStackSize)
        {
            ThrowEvmStackOverflowException();
        }

        return ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), head * WordSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Word PushedHead()
        => ref Unsafe.As<byte, Word>(ref PushBytesRef());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Word CreateWordFromUInt64(ulong value)
        => Vector256.Create(0UL, 0UL, 0UL, value).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushBytes<TTracingInst>(scoped ReadOnlySpan<byte> value)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(value);

        // Source is big-endian bytes. Build big-endian word, then byte-swap to native.
        ref byte bytes = ref PushBytesRef();
        if (value.Length >= WordSize)
        {
            Debug.Assert(value.Length == WordSize, "Trying to push more than 32 bytes to the stack.");
            Unsafe.As<byte, Word>(ref bytes) = ByteSwapWord(Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(value)));
        }
        else
        {
            Unsafe.As<byte, Word>(ref bytes) = default; // Clear first
            value.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref bytes, WordSize - value.Length), value.Length));
            Unsafe.As<byte, Word>(ref bytes) = ByteSwapWord(Unsafe.As<byte, Word>(ref bytes));
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushBytes<TTracingInst>(scoped in ZeroPaddedSpan value)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(value);

        // Source is big-endian bytes. Build big-endian word, then byte-swap to native.
        ref byte bytes = ref PushBytesRef();
        ReadOnlySpan<byte> valueSpan = value.Span;
        if (valueSpan.Length >= WordSize)
        {
            Debug.Assert(value.Length == WordSize, "Trying to push more than 32 bytes to the stack.");
            Unsafe.As<byte, Word>(ref bytes) = ByteSwapWord(Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(valueSpan)));
        }
        else
        {
            Unsafe.As<byte, Word>(ref bytes) = default; // Clear first
            valueSpan.CopyTo(MemoryMarshal.CreateSpan(ref bytes, value.Length));
            Unsafe.As<byte, Word>(ref bytes) = ByteSwapWord(Unsafe.As<byte, Word>(ref bytes));
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushByte<TTracingInst>(byte value)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(value);

        // Native-endian: byte value goes into u0 (first 8-byte lane, no shift).
        ref Word head = ref PushedHead();
        head = Vector256.Create((ulong)value, 0UL, 0UL, 0UL).AsByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Push2Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        // ushort size
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in value, sizeof(ushort));

        ref Word head = ref PushedHead();
        // Source is big-endian bytecode. Reverse to native LE and store in u0.
        ulong lane0 = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ushort>(ref value));

        head = Vector256.Create(lane0, 0UL, 0UL, 0UL).AsByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Push4Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        // uint size
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in value, sizeof(uint));

        ref Word head = ref PushedHead();
        // Source is big-endian bytecode. Reverse to native LE and store in u0.
        ulong lane0 = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, uint>(ref value));

        head = Vector256.Create(lane0, 0UL, 0UL, 0UL).AsByte();
    }

    /// <summary>
    /// Pushes N big-endian bytes (3, 5, 6, or 7) onto the stack as native UInt256.
    /// Uses overlapping 8-byte read — caller must ensure at least 8 readable bytes from value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushSmallBytes<TTracingInst>(ref byte value, int n)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in value, n);

        ref Word head = ref PushedHead();
        // Read 8 bytes, bswap to get MSB-first value in high bits, shift right to keep only n bytes
        ulong lane0 = BinaryPrimitives.ReverseEndianness(
            Unsafe.As<byte, ulong>(ref value)) >> ((8 - n) << 3);
        head = Vector256.Create(lane0, 0UL, 0UL, 0UL).AsByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Push8Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        // ulong size
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in value, sizeof(ulong));

        ref Word head = ref PushedHead();
        // Source is big-endian bytecode. Reverse to native LE and store in u0.
        ulong lane0 = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ulong>(ref value));

        head = Vector256.Create(lane0, 0UL, 0UL, 0UL).AsByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Push16Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        // UInt128 size — source is big-endian bytecode, convert to native UInt256 format.
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in value, sizeof(HalfWord));

        ref Word head = ref PushedHead();
        // Big-endian 16 bytes: [MSB ... LSB] → native UInt256: u0 = reversed(last 8), u1 = reversed(first 8)
        ulong lane0 = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ulong>(ref Unsafe.Add(ref value, 8)));
        ulong lane1 = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ulong>(ref value));
        head = Vector256.Create(lane0, lane1, 0UL, 0UL).AsByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push20Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        // Address size — source is big-endian 20 bytes, convert to native UInt256 format.
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in value, 20);

        ref Word head = ref PushedHead();
        // Big-endian 20 bytes at positions [0..19] → native UInt256:
        // u0 = reversed(bytes[12..19]), u1 = reversed(bytes[4..11]), u2 = reversed(bytes[0..3]) as low 4 bytes
        ulong lane0 = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ulong>(ref Unsafe.Add(ref value, 12)));
        ulong lane1 = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ulong>(ref Unsafe.Add(ref value, 4)));
        ulong lane2 = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, uint>(ref value));

        head = Vector256.Create(lane0, lane1, lane2, 0UL).AsByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushAddress<TTracingInst>(Address address)
        where TTracingInst : struct, IFlag
        => Push20Bytes<TTracingInst>(ref MemoryMarshal.GetArrayDataReference(address.Bytes));

    /// <summary>
    /// Pushes N big-endian bytes (9 ≤ N ≤ 15) onto the stack as native UInt256.
    /// Uses overlapping 8-byte reads for both lanes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push9to15Bytes<TTracingInst>(ref byte value, int n)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in value, n);

        ref Word head = ref PushedHead();
        // Full lower lane: last 8 bytes
        ulong lane0 = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ulong>(ref Unsafe.Add(ref value, n - 8)));
        // Partial upper lane: overlapping 8-byte read from start, shift to keep top (n-8) bytes
        ulong lane1 = BinaryPrimitives.ReverseEndianness(
            Unsafe.As<byte, ulong>(ref value)) >> ((16 - n) << 3);
        head = Vector256.Create(lane0, lane1, 0UL, 0UL).AsByte();
    }

    /// <summary>
    /// Pushes N big-endian bytes (17 ≤ N ≤ 23) onto the stack as native UInt256.
    /// Uses overlapping 8-byte reads for the partial top lane.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push17to23Bytes<TTracingInst>(ref byte value, int n)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in value, n);

        ref Word head = ref PushedHead();
        ulong lane0 = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ulong>(ref Unsafe.Add(ref value, n - 8)));
        ulong lane1 = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ulong>(ref Unsafe.Add(ref value, n - 16)));
        // Overlapping 8-byte read for partial top lane, shift to keep top (n-16) bytes
        ulong lane2 = BinaryPrimitives.ReverseEndianness(
            Unsafe.As<byte, ulong>(ref value)) >> ((24 - n) << 3);
        head = Vector256.Create(lane0, lane1, lane2, 0UL).AsByte();
    }

    /// <summary>
    /// Pushes exactly 24 big-endian bytes onto the stack as native UInt256.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push24Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in value, 24);

        ref Word head = ref PushedHead();
        ulong lane0 = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ulong>(ref Unsafe.Add(ref value, 16)));
        ulong lane1 = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ulong>(ref Unsafe.Add(ref value, 8)));
        ulong lane2 = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ulong>(ref value));
        head = Vector256.Create(lane0, lane1, lane2, 0UL).AsByte();
    }

    /// <summary>
    /// Pushes N big-endian bytes (25 ≤ N ≤ 31) onto the stack as native UInt256.
    /// Uses overlapping 8-byte reads for the partial top lane.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push25to31Bytes<TTracingInst>(ref byte value, int n)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in value, n);

        ref Word head = ref PushedHead();
        ulong lane0 = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ulong>(ref Unsafe.Add(ref value, n - 8)));
        ulong lane1 = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ulong>(ref Unsafe.Add(ref value, n - 16)));
        ulong lane2 = BinaryPrimitives.ReverseEndianness(Unsafe.As<byte, ulong>(ref Unsafe.Add(ref value, n - 24)));
        // Overlapping 8-byte read for partial top lane, shift to keep top (n-24) bytes
        ulong lane3 = BinaryPrimitives.ReverseEndianness(
            Unsafe.As<byte, ulong>(ref value)) >> ((32 - n) << 3);
        head = Vector256.Create(lane0, lane1, lane2, lane3).AsByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push32Bytes<TTracingInst>(in Word value)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.TraceWord(in value);

        // Source is big-endian 32 bytes. Byte-swap to native UInt256 format.
        ref Word head = ref PushedHead();
        head = ByteSwapWord(value);
    }

    /// <summary>
    /// Byte-swap a 32-byte big-endian Word to native UInt256 format (or vice versa).
    /// Reverses byte order within each 8-byte lane and swaps lane order.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Word ByteSwapWord(Word data)
    {
        if (Avx2.IsSupported)
        {
            Word shuffle = Vector256.Create(
                0x18191a1b1c1d1e1ful,
                0x1011121314151617ul,
                0x08090a0b0c0d0e0ful,
                0x0001020304050607ul).AsByte();
            if (Avx512Vbmi.VL.IsSupported)
            {
                return Avx512Vbmi.VL.PermuteVar32x8(data, shuffle);
            }
            else
            {
                Word convert = Avx2.Shuffle(data, shuffle);
                Vector256<ulong> permute = Avx2.Permute4x64(Unsafe.As<Word, Vector256<ulong>>(ref convert), 0b_01_00_11_10);
                return Unsafe.As<Vector256<ulong>, Word>(ref permute);
            }
        }
        else
        {
            // Scalar fallback: reverse bytes within each 8-byte lane and swap lanes
            ulong u0 = BinaryPrimitives.ReverseEndianness(data.AsUInt64().GetElement(3));
            ulong u1 = BinaryPrimitives.ReverseEndianness(data.AsUInt64().GetElement(2));
            ulong u2 = BinaryPrimitives.ReverseEndianness(data.AsUInt64().GetElement(1));
            ulong u3 = BinaryPrimitives.ReverseEndianness(data.AsUInt64().GetElement(0));
            return Vector256.Create(u0, u1, u2, u3).AsByte();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push32Bytes<TTracingInst>(in ValueHash256 hash)
        where TTracingInst : struct, IFlag
        => Push32Bytes<TTracingInst>(in Unsafe.As<ValueHash256, Word>(ref Unsafe.AsRef(in hash)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushLeftPaddedBytes<TTracingInst>(ReadOnlySpan<byte> value, int paddingLength)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(value);

        // Source is big-endian bytes from bytecode. Build a big-endian padded word, then byte-swap to native.
        ref byte bytes = ref PushBytesRef();
        if (value.Length != WordSize)
        {
            // Clear, copy big-endian data, then byte-swap.
            Unsafe.As<byte, Word>(ref bytes) = default;
            value.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref bytes, WordSize - paddingLength), value.Length));
            Unsafe.As<byte, Word>(ref bytes) = ByteSwapWord(Unsafe.As<byte, Word>(ref bytes));
        }
        else
        {
            Unsafe.As<byte, Word>(ref bytes) = ByteSwapWord(Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(value)));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushOne<TTracingInst>()
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(Bytes.OneByteSpan);

        // Native-endian: value 1 → u0 = 1.
        PushedHead() = Vector256.Create(1UL, 0UL, 0UL, 0UL).AsByte();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushZero<TTracingInst>()
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(Bytes.ZeroByteSpan);

        // Single 32-byte store: Zero
        PushedHead() = default;
    }

    public unsafe void PushUInt32<TTracingInst>(uint value)
        where TTracingInst : struct, IFlag
    {
        // Native-endian: uint value goes directly into u0 lane (no endian swap needed).
        if (TTracingInst.IsActive)
        {
            uint be = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
            _tracer.TraceBytes(in Unsafe.As<uint, byte>(ref be), sizeof(uint));
        }

        PushedHead() = Vector256.Create((ulong)value, 0UL, 0UL, 0UL).AsByte();
    }

    public unsafe void PushUInt64<TTracingInst>(ulong value)
        where TTracingInst : struct, IFlag
    {
        // Native-endian: ulong value goes directly into u0 lane (no endian swap needed).
        if (TTracingInst.IsActive)
        {
            ulong be = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
            _tracer.TraceBytes(in Unsafe.As<ulong, byte>(ref be), sizeof(ulong));
        }

        PushedHead() = Vector256.Create(value, 0UL, 0UL, 0UL).AsByte();
    }

    /// <summary>
    /// Pushes a UInt256 onto the stack.
    /// Stack stores values in native (little-endian) UInt256 format — no byte-swap needed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushUInt256<TTracingInst>(in UInt256 value)
        where TTracingInst : struct, IFlag
    {
        ref byte head = ref PushBytesRef();
        Unsafe.WriteUnaligned(ref head, value);

        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(MemoryMarshal.CreateReadOnlySpan(ref head, WordSize));
    }

    public void PushSignedInt256<TTracingInst>(in Int256.Int256 value)
        where TTracingInst : struct, IFlag
    {
        // tail call into UInt256
        PushUInt256<TTracingInst>(in Unsafe.As<Int256.Int256, UInt256>(ref Unsafe.AsRef(in value)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PopLimbo()
    {
        if (Head-- == 0)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Pops a UInt256 from the stack.
    /// Stack stores values in native (little-endian) UInt256 format — no byte-swap needed.
    /// </summary>
    /// <param name="result">The returned value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PopUInt256(out UInt256 result)
    {
        Unsafe.SkipInit(out result);
        ref byte bytes = ref PopBytesByRef();
        if (Unsafe.IsNullRef(ref bytes)) return false;

        result = Unsafe.ReadUnaligned<UInt256>(ref bytes);
        return true;
    }

    /// <summary>
    /// Reads the top UInt256 from the stack without popping.
    /// Stack stores values in native (little-endian) UInt256 format — no byte-swap needed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PeekUInt256(out UInt256 result)
    {
        Unsafe.SkipInit(out result);
        ref byte bytes = ref PeekBytesByRef();
        if (Unsafe.IsNullRef(ref bytes)) return false;

        result = Unsafe.ReadUnaligned<UInt256>(ref bytes);
        return true;
    }

    /// <summary>
    /// Writes a UInt256 to the current stack top.
    /// Stack stores values in native (little-endian) UInt256 format — no byte-swap needed.
    /// Does NOT change the head pointer — use after <see cref="PeekUInt256"/> to modify in place.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReplaceTopUInt256(in UInt256 value)
    {
        int head = Head;
        ref byte top = ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), (head - 1) * WordSize);
        Unsafe.WriteUnaligned(ref top, value);
    }

    public readonly bool PeekUInt256IsZero()
    {
        int head = Head;
        if (head-- == 0)
        {
            return false;
        }

        ref byte bytes = ref _bytes[head * WordSize];
        return Unsafe.ReadUnaligned<UInt256>(ref bytes).IsZero;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref byte PeekBytesByRef()
    {
        int head = Head;
        if (head-- == 0)
        {
            return ref Unsafe.NullRef<byte>();
        }
        return ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), head * WordSize);
    }

    public readonly Span<byte> PeekWord256()
    {
        int head = Head;
        if (head-- == 0)
        {
            ThrowEvmStackUnderflowException();
        }

        return _bytes.Slice(head * WordSize, WordSize);
    }

    public Address? PopAddress()
    {
        if (Head-- == 0) return null;
        // Byte-swap native stack word to big-endian, then extract last 20 bytes (address).
        Word be = ByteSwapWord(Unsafe.ReadUnaligned<Word>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), Head * WordSize)));
        byte[] addrBytes = new byte[AddressSize];
        Unsafe.CopyBlockUnaligned(ref addrBytes[0], ref Unsafe.Add(ref Unsafe.As<Word, byte>(ref be), WordSize - AddressSize), AddressSize);
        return new Address(addrBytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Address? PopAddressCached(ref Address? cached)
    {
        if (Head-- == 0) return null;

        // Byte-swap native stack word to big-endian for address extraction.
        Word be = ByteSwapWord(Unsafe.ReadUnaligned<Word>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), Head * WordSize)));
        ref byte stackRef = ref Unsafe.Add(ref Unsafe.As<Word, byte>(ref be), WordSize - AddressSize);
        if (cached is not null)
        {
            ref byte cachedRef = ref MemoryMarshal.GetArrayDataReference(cached.Bytes);
            if (Unsafe.As<byte, Vector128<byte>>(ref stackRef) == Unsafe.As<byte, Vector128<byte>>(ref cachedRef) &&
                Unsafe.As<byte, uint>(ref Unsafe.Add(ref stackRef, 16)) == Unsafe.As<byte, uint>(ref Unsafe.Add(ref cachedRef, 16)))
            {
                return cached;
            }
        }

        byte[] bytes = new byte[AddressSize];
        Unsafe.CopyBlockUnaligned(ref bytes[0], ref stackRef, AddressSize);
        Address addr = new Address(bytes);
        cached = addr;
        return addr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref byte PopBytesByRef()
    {
        int head = Head;
        if (head == 0)
        {
            return ref Unsafe.NullRef<byte>();
        }
        head--;
        Head = head;
        return ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), head * WordSize);
    }

    /// <summary>
    /// Pops 32 bytes from the stack and returns them in big-endian format.
    /// The data is byte-swapped in-place from native format since it's below the stack pointer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> PopWord256()
    {
        ref byte bytes = ref PopBytesByRef();
        if (Unsafe.IsNullRef(ref bytes)) ThrowEvmStackUnderflowException();

        // Byte-swap in-place: native → big-endian. Safe because data is below Head (already popped).
        Unsafe.As<byte, Word>(ref bytes) = ByteSwapWord(Unsafe.As<byte, Word>(ref bytes));
        return MemoryMarshal.CreateSpan(ref bytes, WordSize);
    }

    /// <summary>
    /// Pops 32 bytes from the stack and returns them in big-endian format.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PopWord256(out Span<byte> word)
    {
        if (Head-- == 0)
        {
            word = default;
            return false;
        }

        ref byte bytes = ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), Head * WordSize);
        // Byte-swap in-place: native → big-endian.
        Unsafe.As<byte, Word>(ref bytes) = ByteSwapWord(Unsafe.As<byte, Word>(ref bytes));
        word = MemoryMarshal.CreateSpan(ref bytes, WordSize);
        return true;
    }

    public byte PopByte()
    {
        ref byte bytes = ref PopBytesByRef();

        if (Unsafe.IsNullRef(ref bytes)) ThrowEvmStackUnderflowException();

        // Native-endian: least significant byte is at offset 0 (u0 LSB).
        return bytes;
    }

    [SkipLocalsInit]
    public EvmExceptionType Dup<TTracingInst>(int depth)
        where TTracingInst : struct, IFlag
    {
        int head = Head;
        if (head < depth) goto StackUnderflow;

        ref byte bytes = ref MemoryMarshal.GetReference(_bytes);

        ref byte from = ref Unsafe.Add(ref bytes, (head - depth) * WordSize);
        ref byte to = ref Unsafe.Add(ref bytes, head * WordSize);

        Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<Word>(ref from));

        if (TTracingInst.IsActive) Trace(depth);

        if (++head >= MaxStackSize) goto StackOverflow;

        Head = head;

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    StackOverflow:
        return EvmExceptionType.StackOverflow;
    }

    public readonly bool EnsureDepth(int depth)
        => Head >= depth;

    [SkipLocalsInit]
    public readonly EvmExceptionType Swap<TTracingInst>(int depth)
        where TTracingInst : struct, IFlag
    {
        int head = Head;
        if (head < depth) goto StackUnderflow;

        ref byte bytes = ref MemoryMarshal.GetReference(_bytes);

        ref byte bottom = ref Unsafe.Add(ref bytes, (head - depth) * WordSize);
        ref byte top = ref Unsafe.Add(ref bytes, (head - 1) * WordSize);

        Word buffer = Unsafe.ReadUnaligned<Word>(ref bottom);
        Unsafe.WriteUnaligned(ref bottom, Unsafe.ReadUnaligned<Word>(ref top));
        Unsafe.WriteUnaligned(ref top, buffer);

        if (TTracingInst.IsActive) Trace(depth);

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    public readonly bool Exchange<TTracingInst>(int n, int m)
        where TTracingInst : struct, IFlag
    {
        int maxDepth = Math.Max(n, m);
        if (!EnsureDepth(maxDepth)) return false;

        ref byte bytes = ref MemoryMarshal.GetReference(_bytes);

        ref byte first = ref Unsafe.Add(ref bytes, (Head - n) * WordSize);
        ref byte second = ref Unsafe.Add(ref bytes, (Head - m) * WordSize);

        Word buffer = Unsafe.ReadUnaligned<Word>(ref first);
        Unsafe.WriteUnaligned(ref first, Unsafe.ReadUnaligned<Word>(ref second));
        Unsafe.WriteUnaligned(ref second, buffer);

        if (TTracingInst.IsActive) Trace(maxDepth);

        return true;
    }

    private readonly void Trace(int depth)
    {
        for (int i = depth; i > 0; i--)
        {
            // Stack stores native-endian; byte-swap to big-endian for tracer.
            Word bigEndian = ByteSwapWord(Unsafe.ReadUnaligned<Word>(
                ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), (Head - i) * WordSize)));
            _tracer.ReportStackPush(MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(in bigEndian, 1)));
        }
    }

    [StackTraceHidden]
    [DoesNotReturn]
    internal static void ThrowEvmStackUnderflowException()
    {
        Metrics.EvmExceptions++;
        throw new EvmStackUnderflowException();
    }

    [StackTraceHidden]
    [DoesNotReturn]
    internal static void ThrowEvmStackOverflowException()
    {
        Metrics.EvmExceptions++;
        throw new EvmStackOverflowException();
    }
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;

namespace Nethermind.Evm;

using Word = Vector256<byte>;
using static Unsafe;

internal static partial class EvmInstructions
{
    /// <summary>
    /// Pops a value from the EVM stack.
    /// Deducts the base gas cost and returns an exception if the stack is underflowed.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">The gas state which is reduced by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns><see cref="EvmExceptionType.None"/> if successful; otherwise, <see cref="EvmExceptionType.StackUnderflow"/>.</returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EvmExceptionType InstructionPop<TGasPolicy>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        TGasPolicy.Consume(ref gas, GasCostOf.Base);
        // Pop from the stack; if nothing to pop, signal a stack underflow.
        return stack.PopLimbo() ? EvmExceptionType.None : EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Interface for series of items based operations.
    /// The <c>Count</c> property specifies the expected number of items.
    /// </summary>
    public interface IOpCount
    {
        /// <summary>
        /// The number of items expected.
        /// </summary>
        abstract static int Count { get; }

        /// <summary>
        /// This is the default implementation for push operations.
        /// Pushes immediate data from the code onto the stack.
        /// If insufficient bytes are available, pads the value to the expected length.
        /// </summary>
        /// <param name="length">The expected length of the data.</param>
        /// <param name="stack">The execution stack.</param>
        /// <param name="programCounter">The program counter.</param>
        /// <param name="code">The code segment containing the immediate data.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        virtual static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            // Use available bytes and pad left if fewer than expected.
            int usedFromCode = Math.Min(code.Length - programCounter, length);
            stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), length);
        }
    }

    // Some push operations override the default Push method to handle fixed-size optimizations.

    /// <summary>
    /// 0 item operations.
    /// </summary>
    public struct Op0 : IOpCount { public static int Count => 0; }

    /// <summary>
    /// 1 item operations.
    /// </summary>
    public struct Op1 : IOpCount
    {
        const int Size = sizeof(byte);
        public static int Count => Size;

        /// <summary>
        /// Push operation for a single byte.
        /// If exactly one byte is available, it is pushed; otherwise, zero is pushed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            // Determine how many bytes can be used from the code.
            int usedFromCode = Math.Min(code.Length - programCounter, length);
            if (usedFromCode == Size)
            {
                // Directly push the single byte.
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.PushByte<TTracingInst>(Add(ref bytes, programCounter));
            }
            else
            {
                // Fallback when immediate data is incomplete.
                stack.PushZero<TTracingInst>();
            }
        }
    }

    /// <summary>
    /// 2 item operations.
    /// </summary>
    public struct Op2 : IOpCount { public static int Count => 2; }

    /// <summary>
    /// Push operation for two bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EvmExceptionType InstructionPush2<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        const int Size = sizeof(ushort);
        TGasPolicy.Consume(ref gas, GasCostOf.VeryLow);
        // Retrieve the code segment containing immediate data.
        ReadOnlySpan<byte> code = vm.VmState.Env.CodeInfo.CodeSpan;

        ref byte bytes = ref MemoryMarshal.GetReference(code);
        int remainingCode = code.Length - programCounter;
        Instruction nextInstruction;
        if (!TTracingInst.IsActive &&
            remainingCode > Size &&
            stack.Head < EvmStack.MaxStackSize - 1 &&
            ((nextInstruction = (Instruction)Add(ref bytes, programCounter + Size))
                is Instruction.JUMP or Instruction.JUMPI))
        {
            // If next instruction is a JUMP we can skip the PUSH+POP from stack
            ushort destination = As<byte, ushort>(ref Add(ref bytes, programCounter));
            if (BitConverter.IsLittleEndian)
            {
                destination = BinaryPrimitives.ReverseEndianness(destination);
            }

            if (nextInstruction == Instruction.JUMP)
            {
                TGasPolicy.Consume(ref gas, GasCostOf.Jump);
                vm.OpCodeCount++;
            }
            else
            {
                TGasPolicy.Consume(ref gas, GasCostOf.JumpI);
                vm.OpCodeCount++;
                bool shouldJump = TestJumpCondition(ref stack, out bool isOverflow);
                if (isOverflow) goto StackUnderflow;
                if (!shouldJump)
                {
                    // Move forward by 2 bytes + JUMPI
                    programCounter += Size + 1;
                    goto Success;
                }
            }

            // Validate the jump destination and update the program counter if valid.
            if (!Jump((int)destination, ref programCounter, vm.VmState.Env))
                goto InvalidJumpDestination;

            goto Success;
        }
        else if (remainingCode >= Size)
        {
            // Optimized push for exactly two bytes.
            stack.Push2Bytes<TTracingInst>(ref Add(ref bytes, programCounter));
        }
        else if (remainingCode == Op1.Count)
        {
            // Directly push the single byte.
            stack.PushByte<TTracingInst>(Add(ref bytes, programCounter));
        }
        else
        {
            // Fallback when immediate data is incomplete.
            stack.PushZero<TTracingInst>();
        }

        programCounter += Size;
    Success:
        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    InvalidJumpDestination:
        return EvmExceptionType.InvalidJumpDestination;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// 3 item operations.
    /// </summary>
    public struct Op3 : IOpCount
    {
        const int Size = 3;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int remainingCode = code.Length - programCounter;
            if (remainingCode >= 8)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.PushSmallBytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                int usedFromCode = Math.Min(remainingCode, Size);
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 4 item operations.
    /// </summary>
    public struct Op4 : IOpCount
    {
        const int Size = sizeof(uint);
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, length);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                // Direct push of a 4-byte value.
                stack.Push4Bytes<TTracingInst>(ref Add(ref bytes, programCounter));
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), length);
            }
        }
    }

    /// <summary>
    /// 5 item operations.
    /// </summary>
    public struct Op5 : IOpCount
    {
        const int Size = 5;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int remainingCode = code.Length - programCounter;
            if (remainingCode >= 8)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.PushSmallBytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                int usedFromCode = Math.Min(remainingCode, Size);
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 6 item operations.
    /// </summary>
    public struct Op6 : IOpCount
    {
        const int Size = 6;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int remainingCode = code.Length - programCounter;
            if (remainingCode >= 8)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.PushSmallBytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                int usedFromCode = Math.Min(remainingCode, Size);
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 7 item operations.
    /// </summary>
    public struct Op7 : IOpCount
    {
        const int Size = 7;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int remainingCode = code.Length - programCounter;
            if (remainingCode >= 8)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.PushSmallBytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                int usedFromCode = Math.Min(remainingCode, Size);
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 8 item operations.
    /// </summary>
    public struct Op8 : IOpCount
    {
        const int Size = sizeof(ulong);
        public static int Count => Size;

        /// <summary>
        /// Push operation for eight bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, length);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push8Bytes<TTracingInst>(ref Add(ref bytes, programCounter));
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), length);
            }
        }
    }

    /// <summary>
    /// 9 item operations.
    /// </summary>
    public struct Op9 : IOpCount
    {
        const int Size = 9;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push9to15Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 10 item operations.
    /// </summary>
    public struct Op10 : IOpCount
    {
        const int Size = 10;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push9to15Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 11 item operations.
    /// </summary>
    public struct Op11 : IOpCount
    {
        const int Size = 11;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push9to15Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 12 item operations.
    /// </summary>
    public struct Op12 : IOpCount
    {
        const int Size = 12;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push9to15Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 13 item operations.
    /// </summary>
    public struct Op13 : IOpCount
    {
        const int Size = 13;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push9to15Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 14 item operations.
    /// </summary>
    public struct Op14 : IOpCount
    {
        const int Size = 14;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push9to15Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 15 item operations.
    /// </summary>
    public struct Op15 : IOpCount
    {
        const int Size = 15;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push9to15Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    public struct Op16 : IOpCount
    {
        const int Size = 16;
        public static int Count => Size;

        /// <summary>
        /// Push operation for 16 bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, length);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push16Bytes<TTracingInst>(ref Add(ref bytes, programCounter));
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), length);
            }
        }
    }

    /// <summary>
    /// 17 item operations.
    /// </summary>
    public struct Op17 : IOpCount
    {
        const int Size = 17;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push17to23Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 18 item operations.
    /// </summary>
    public struct Op18 : IOpCount
    {
        const int Size = 18;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push17to23Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 19 item operations.
    /// </summary>
    public struct Op19 : IOpCount
    {
        const int Size = 19;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push17to23Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 20 item operations.
    /// </summary>
    public struct Op20 : IOpCount
    {
        const int Size = 20;
        public static int Count => Size;

        /// <summary>
        /// Push operation for 20 bytes (commonly used for addresses).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, length);
            if (usedFromCode == Size)
            {
                // Optimized push for address size data.
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push20Bytes<TTracingInst>(ref Add(ref bytes, programCounter));
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), length);
            }
        }
    }


    /// <summary>
    /// 21 item operations.
    /// </summary>
    public struct Op21 : IOpCount
    {
        const int Size = 21;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push17to23Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 22 item operations.
    /// </summary>
    public struct Op22 : IOpCount
    {
        const int Size = 22;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push17to23Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 23 item operations.
    /// </summary>
    public struct Op23 : IOpCount
    {
        const int Size = 23;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push17to23Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 24 item operations.
    /// </summary>
    public struct Op24 : IOpCount
    {
        const int Size = 24;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push24Bytes<TTracingInst>(ref Add(ref bytes, programCounter));
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 25 item operations.
    /// </summary>
    public struct Op25 : IOpCount
    {
        const int Size = 25;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push25to31Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 26 item operations.
    /// </summary>
    public struct Op26 : IOpCount
    {
        const int Size = 26;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push25to31Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 27 item operations.
    /// </summary>
    public struct Op27 : IOpCount
    {
        const int Size = 27;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push25to31Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 28 item operations.
    /// </summary>
    public struct Op28 : IOpCount
    {
        const int Size = 28;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push25to31Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 29 item operations.
    /// </summary>
    public struct Op29 : IOpCount
    {
        const int Size = 29;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push25to31Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 30 item operations.
    /// </summary>
    public struct Op30 : IOpCount
    {
        const int Size = 30;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push25to31Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 31 item operations.
    /// </summary>
    public struct Op31 : IOpCount
    {
        const int Size = 31;
        public static int Count => Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, Size);
            if (usedFromCode == Size)
            {
                ref byte bytes = ref MemoryMarshal.GetReference(code);
                stack.Push25to31Bytes<TTracingInst>(ref Add(ref bytes, programCounter), Size);
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), Size);
            }
        }
    }

    /// <summary>
    /// 32 item operations.
    /// </summary>
    public struct Op32 : IOpCount
    {
        const int Size = 32;
        public static int Count => Size;

        /// <summary>
        /// Push operation for 32 bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push<TTracingInst>(int length, ref EvmStack stack, int programCounter, ReadOnlySpan<byte> code)
            where TTracingInst : struct, IFlag
        {
            int usedFromCode = Math.Min(code.Length - programCounter, length);
            if (usedFromCode == Size)
            {
                // Leverage reinterpretation of bytes as a 256-bit vector.
                stack.Push32Bytes<TTracingInst>(in As<byte, Word>(ref Add(ref MemoryMarshal.GetReference(code), programCounter)));
            }
            else
            {
                stack.PushLeftPaddedBytes<TTracingInst>(code.Slice(programCounter, usedFromCode), length);
            }
        }
    }

    /// <summary>
    /// Handles the PUSH0 opcode which pushes a zero onto the stack.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">The gas state which is reduced by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns><see cref="EvmExceptionType.None"/> on success.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionPush0<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume(ref gas, GasCostOf.Base);
        stack.PushZero<TTracingInst>();
        return EvmExceptionType.None;
    }

    /// <summary>
    /// Executes a PUSH instruction.
    /// Reads immediate data of a fixed length from the code and pushes it onto the stack.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy implementation.</typeparam>
    /// <typeparam name="TOpCount">The push operation implementation defining the byte count.</typeparam>
    /// <typeparam name="TTracingInst">The tracing flag.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">The gas state which is reduced by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter, which will be advanced.</param>
    /// <returns><see cref="EvmExceptionType.None"/> on success.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionPush<TGasPolicy, TOpCount, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCount : struct, IOpCount
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume(ref gas, GasCostOf.VeryLow);
        // Retrieve the code segment containing immediate data.
        ReadOnlySpan<byte> code = vm.VmState.Env.CodeInfo.CodeSpan;
        // Use the push method defined by the specific push operation.
        TOpCount.Push<TTracingInst>(TOpCount.Count, ref stack, programCounter, code);
        // Advance the program counter by the number of bytes consumed.
        programCounter += TOpCount.Count;
        return EvmExceptionType.None;
    }

    /// <summary>
    /// Executes a DUP operation which duplicates the nth stack element.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy implementation.</typeparam>
    /// <typeparam name="TOpCount">The duplicate operation implementation that defines which element to duplicate.</typeparam>
    /// <typeparam name="TTracingInst">The tracing flag.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">The gas state which is reduced by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter.</param>
    /// <returns><see cref="EvmExceptionType.None"/> on success or <see cref="EvmExceptionType.StackUnderflow"/> if insufficient stack elements.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionDup<TGasPolicy, TOpCount, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCount : struct, IOpCount
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume(ref gas, GasCostOf.VeryLow);
        return stack.Dup<TTracingInst>(TOpCount.Count);
    }

    /// <summary>
    /// Executes a SWAP operation which swaps the top element with the (n+1)th element.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy implementation.</typeparam>
    /// <typeparam name="TOpCount">The swap operation implementation that defines the swap depth.</typeparam>
    /// <typeparam name="TTracingInst">The tracing flag.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">The gas state which is reduced by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter.</param>
    /// <returns><see cref="EvmExceptionType.None"/> on success or <see cref="EvmExceptionType.StackUnderflow"/> if insufficient elements.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionSwap<TGasPolicy, TOpCount, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCount : struct, IOpCount
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume(ref gas, GasCostOf.VeryLow);
        return stack.Swap<TTracingInst>(TOpCount.Count + 1);
    }

    /// <summary>
    /// Executes a LOG operation which records a log entry with topics and data.
    /// Pops data offset and length, then pops a fixed number of topics from the stack.
    /// Validates memory expansion and deducts gas accordingly.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy implementation.</typeparam>
    /// <typeparam name="TOpCount">Specifies the number of log topics (as defined by its Count property).</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">The gas state which is reduced by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if the log is successfully recorded; otherwise, an appropriate exception type such as
    /// <see cref="EvmExceptionType.StackUnderflow"/>, <see cref="EvmExceptionType.StaticCallViolation"/>, or <see cref="EvmExceptionType.OutOfGas"/>.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionLog<TGasPolicy, TOpCount>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCount : struct, IOpCount
    {
        VmState<TGasPolicy> vmState = vm.VmState;
        // Logging is not permitted in static call contexts.
        if (vmState.IsStatic) goto StaticCallViolation;

        // Pop memory offset and length for the log data.
        if (!stack.PopUInt256(out UInt256 position) || !stack.PopUInt256(out UInt256 length)) goto StackUnderflow;

        // The number of topics is defined by the generic parameter.
        long topicsCount = TOpCount.Count;

        // Ensure that the memory expansion for the log data is accounted for.
        if (!TGasPolicy.UpdateMemoryCost(ref gas, in position, length, vmState)) goto OutOfGas;
        // Deduct gas for the log entry itself, including per-topic and per-byte data costs.
        long dataSize = (long)length;
        if (!TGasPolicy.ConsumeLogEmission(ref gas, topicsCount, dataSize)) goto OutOfGas;

        // Load the log data from memory.
        if (!vmState.Memory.TryLoad(in position, length, out ReadOnlyMemory<byte> data))
            goto OutOfGas;

        // Pop topics as value types — no heap allocation.
        ValueHash256 t0 = default, t1 = default, t2 = default, t3 = default;
        if (topicsCount > 0) t0 = new ValueHash256(stack.PopWord256());
        if (topicsCount > 1) t1 = new ValueHash256(stack.PopWord256());
        if (topicsCount > 2) t2 = new ValueHash256(stack.PopWord256());
        if (topicsCount > 3) t3 = new ValueHash256(stack.PopWord256());

        // Store compactly in the journal — data bytes are copied into a flat buffer, no per-LOG allocations.
        LogJournal logs = vmState.AccessTracker.Logs;
        logs.AddEntry(vmState.Env.ExecutingAccount, data.Span, (int)topicsCount, in t0, in t1, in t2, in t3);

        // Optionally report the log if tracing is enabled (rare path — materializes LogEntry on demand).
        if (vm.TxTracer.IsTracingLogs)
        {
            vm.TxTracer.ReportLog(logs.MaterializeEntry(logs.Count - 1));
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    StaticCallViolation:
        return EvmExceptionType.StaticCallViolation;
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    }
}

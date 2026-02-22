// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Evm.State;
using static Nethermind.Evm.VirtualMachineStatics;

namespace Nethermind.Evm;

internal static partial class EvmInstructions
{
    /// <summary>
    /// Interface defining the properties for a call-like opcode.
    /// Each implementation specifies whether the call is static and what its execution type is.
    /// </summary>
    public interface IOpCall
    {
        /// <summary>
        /// Indicates if the call is static.
        /// Static calls cannot modify state.
        /// </summary>
        virtual static bool IsStatic => false;

        /// <summary>
        /// Returns the specific execution type of the call.
        /// </summary>
        abstract static ExecutionType ExecutionType { get; }
    }

    /// <summary>
    /// Represents a normal CALL opcode.
    /// </summary>
    public struct OpCall : IOpCall
    {
        public static ExecutionType ExecutionType => ExecutionType.CALL;
    }

    /// <summary>
    /// Represents a CALLCODE opcode.
    /// </summary>
    public struct OpCallCode : IOpCall
    {
        public static ExecutionType ExecutionType => ExecutionType.CALLCODE;
    }

    /// <summary>
    /// Represents a DELEGATECALL opcode.
    /// </summary>
    public struct OpDelegateCall : IOpCall
    {
        public static ExecutionType ExecutionType => ExecutionType.DELEGATECALL;
    }

    /// <summary>
    /// Represents a STATICCALL opcode.
    /// </summary>
    public struct OpStaticCall : IOpCall
    {
        public static bool IsStatic => true;
        public static ExecutionType ExecutionType => ExecutionType.STATICCALL;
    }

    /// <summary>
    /// Executes a call-like operation.
    /// This method handles various call types (CALL, CALLCODE, DELEGATECALL, STATICCALL) by:
    /// - Popping call parameters from the stack,
    /// - Charging appropriate gas for the call and memory expansion,
    /// - Validating call conditions (such as static call restrictions and call depth),
    /// - Performing balance transfers,
    /// - Creating a new execution frame for the call.
    /// </summary>
    /// <typeparam name="TOpCall">
    /// The call opcode type (e.g. <see cref="OpCall"/>, <see cref="OpStaticCall"/>).
    /// </typeparam>
    /// <typeparam name="TTracingInst">
    /// A type implementing <see cref="IFlag"/> that indicates whether instruction tracing is active.
    /// </typeparam>
    /// <param name="vm">The current virtual machine instance containing execution state.</param>
    /// <param name="stack">The EVM stack for retrieving call parameters and pushing results.</param>
    /// <param name="gas">The gas which is updated by the operation's cost.</param>
    /// <param name="programCounter">Reference to the current program counter (not modified by this method).</param>
    /// <returns>
    /// An <see cref="EvmExceptionType"/> value indicating success or the type of error encountered.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionCall<TGasPolicy, TOpCall, TTracingInst>(VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCall : struct, IOpCall
        where TTracingInst : struct, IFlag
    {
        // Increment global call metrics only when tracing.
        if (TTracingInst.IsActive)
            Metrics.IncrementCalls();

        // Clear previous return data.
        vm.ReturnData = null;

        // Pop the gas limit for the call.
        if (!stack.PopUInt256(out UInt256 gasLimit)) goto StackUnderflow;
        // Pop the code source address from the stack, reusing cached address when possible.
        Address codeSource = stack.PopAddressCached(ref vm._cachedCallAddress);
        if (codeSource is null) goto StackUnderflow;

        ExecutionEnvironment env = vm.VmState.Env;
        // Determine the call value based on the call type.
        UInt256 callValue;
        if (typeof(TOpCall) == typeof(OpStaticCall))
        {
            // Static calls cannot transfer value.
            callValue = UInt256.Zero;
        }
        else if (typeof(TOpCall) == typeof(OpDelegateCall))
        {
            // Delegate calls use the value from the current execution context.
            callValue = env.Value;
        }
        else if (!stack.PopUInt256(out callValue))
        {
            goto StackUnderflow;
        }

        // Single-entry code info cache: skip expensive state tree lookup for repeated calls to the same address.
        // Invalidated by CREATE/CREATE2 (which may change code at any address).
        // Uses _cachedCallCodeSource (separate from _cachedCallAddress used by PopAddressCached)
        // so that the Address-object cache doesn't corrupt the CodeInfo cache.
        CodeInfo codeInfo;
        Address delegated;
        if (ReferenceEquals(codeSource, vm._cachedCallCodeSource) && vm._cachedCallCodeInfo is not null)
        {
            codeInfo = vm._cachedCallCodeInfo;
            delegated = vm._cachedCallDelegated;
        }
        else
        {
            codeInfo = vm.CodeInfoRepository.GetCachedCodeInfo(codeSource, vm.Spec, out delegated);
            vm._cachedCallCodeSource = codeSource;
            vm._cachedCallCodeInfo = codeInfo;
            vm._cachedCallDelegated = delegated;
        }

        // For non-delegate calls, the transfer value is the call value.
        UInt256 transferValue = typeof(TOpCall) == typeof(OpDelegateCall) ? UInt256.Zero : callValue;

        // Fast path for zero-value calls to empty-code addresses (post-EIP-2929).
        // Saves: spec interface calls, 63/64 gas dance, frame creation.
        // Correctness: _useHotAndColdStorage implies EIP-158 (ClearEmptyAccountWhenTouched),
        // GetCallCost()=0, and Use63Over64Rule; forward/refund cancels for empty code.
        if (vm._useHotAndColdStorage && transferValue.IsZero &&
            codeInfo.Code.Length == 0 && !codeInfo.IsPrecompile && delegated is null &&
            !TTracingInst.IsActive && !vm.TxTracer.IsTracingActions)
        {
            // Pop remaining 4 args: must read values for memory expansion charging.
            if (!stack.PopUInt256(out UInt256 fpDataOffset) ||
                !stack.PopUInt256(out UInt256 fpDataLength) ||
                !stack.PopUInt256(out UInt256 fpOutputOffset) ||
                !stack.PopUInt256(out UInt256 fpOutputLength))
                goto StackUnderflow;

            // Inline warm/cold check (skip IsPrecompile FrozenSet lookup: empty code = not precompile)
            bool wasCold = vm.VmState.AccessTracker.WarmUp(codeSource);
            long accessGas = wasCold ? GasCostOf.ColdAccountAccess : GasCostOf.WarmStateRead;
            if (!TGasPolicy.UpdateGas(ref gas, accessGas)) goto OutOfGas;

            // Charge memory expansion for input and output regions (required by EVM spec).
            if (!TGasPolicy.UpdateMemoryCost(ref gas, in fpDataOffset, fpDataLength, vm.VmState) ||
                !TGasPolicy.UpdateMemoryCost(ref gas, in fpOutputOffset, fpOutputLength, vm.VmState))
                goto OutOfGas;

            if (env.CallDepth >= MaxCallDepth)
            {
                vm.ReturnDataBuffer = Array.Empty<byte>();
                stack.PushZero<TTracingInst>();
                return EvmExceptionType.None;
            }

            vm.ReturnDataBuffer = default;
            vm.ReturnData = null;
            stack.PushBytes<OffFlag>(StatusCode.SuccessBytes.Span);
            return EvmExceptionType.None;
        }

        // Pop additional parameters: data offset, data length, output offset, and output length.
        if (!stack.PopUInt256(out UInt256 dataOffset) ||
            !stack.PopUInt256(out UInt256 dataLength) ||
            !stack.PopUInt256(out UInt256 outputOffset) ||
            !stack.PopUInt256(out UInt256 outputLength))
            goto StackUnderflow;

        if (!TGasPolicy.ConsumeAccountAccessGasWithDelegation(ref gas, vm.Spec, in vm.VmState.AccessTracker,
                vm.TxTracer.IsTracingAccess, codeSource, delegated)) goto OutOfGas;
        // Enforce static call restrictions: no value transfer allowed unless it's a CALLCODE.
        if (vm.VmState.IsStatic && !transferValue.IsZero && typeof(TOpCall) != typeof(OpCallCode))
            return EvmExceptionType.StaticCallViolation;

        // Determine caller and target based on the call type.
        Address caller = typeof(TOpCall) == typeof(OpDelegateCall) ? env.Caller : env.ExecutingAccount;
        Address target = (typeof(TOpCall) == typeof(OpCall) || typeof(TOpCall) == typeof(OpStaticCall))
            ? codeSource
            : env.ExecutingAccount;

        // Add extra gas cost if value is transferred.
        if (!transferValue.IsZero)
        {
            if (!TGasPolicy.ConsumeCallValueTransfer(ref gas)) goto OutOfGas;
        }

        IReleaseSpec spec = vm.Spec;
        IWorldState state = vm.WorldState;
        // Charge additional gas if the target account is new or considered empty.
        if (!spec.ClearEmptyAccountWhenTouched && !state.AccountExists(target))
        {
            if (!TGasPolicy.ConsumeNewAccountCreation(ref gas)) goto OutOfGas;
        }
        else if (spec.ClearEmptyAccountWhenTouched && transferValue != 0 && state.IsDeadAccount(target))
        {
            if (!TGasPolicy.ConsumeNewAccountCreation(ref gas)) goto OutOfGas;
        }

        // Update gas: call cost and memory expansion for input and output.
        if (!TGasPolicy.UpdateGas(ref gas, spec.GetCallCost()) ||
            !TGasPolicy.UpdateMemoryCost(ref gas, in dataOffset, dataLength, vm.VmState) ||
            !TGasPolicy.UpdateMemoryCost(ref gas, in outputOffset, outputLength, vm.VmState))
            goto OutOfGas;

        // If contract is large, charge for access
        if (spec.IsEip7907Enabled)
        {
            uint excessContractSize = (uint)Math.Max(0, codeInfo.CodeSpan.Length - CodeSizeConstants.MaxCodeSizeEip170);
            if (excessContractSize > 0 && !ChargeForLargeContractAccess(excessContractSize, codeSource, in vm.VmState.AccessTracker, ref gas))
                goto OutOfGas;
        }

        // Get remaining gas for 63/64 calculation
        long gasAvailable = TGasPolicy.GetRemainingGas(in gas);

        // Apply the 63/64 gas rule if enabled.
        if (spec.Use63Over64Rule)
        {
            gasLimit = UInt256.Min((UInt256)(gasAvailable - gasAvailable / 64), gasLimit);
        }

        // If gasLimit exceeds the host's representable range, treat as out-of-gas.
        if (gasLimit >= long.MaxValue) goto OutOfGas;

        long gasLimitUl = (long)gasLimit;
        if (!TGasPolicy.UpdateGas(ref gas, gasLimitUl)) goto OutOfGas;

        // Add call stipend if value is being transferred.
        if (!transferValue.IsZero)
        {
            if (vm.TxTracer.IsTracingRefunds)
                vm.TxTracer.ReportExtraGasPressure(GasCostOf.CallStipend);
            gasLimitUl += GasCostOf.CallStipend;
        }

        // Check call depth and balance of the caller.
        if (env.CallDepth >= MaxCallDepth ||
            (!transferValue.IsZero && state.GetBalance(env.ExecutingAccount) < transferValue))
        {
            // If the call cannot proceed, return an empty response and push zero on the stack.
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero<TTracingInst>();

            // Optionally report memory changes for refund tracing.
            if (vm.TxTracer.IsTracingRefunds)
            {
                // Specific to Parity tracing: inspect 32 bytes from data offset.
                ReadOnlyMemory<byte>? memoryTrace = vm.VmState.Memory.Inspect(in dataOffset, 32);
                vm.TxTracer.ReportMemoryChange(dataOffset, memoryTrace is null ? default : memoryTrace.Value.Span);
            }

            if (TTracingInst.IsActive)
            {
                vm.TxTracer.ReportOperationRemainingGas(TGasPolicy.GetRemainingGas(in gas));
                vm.TxTracer.ReportOperationError(EvmExceptionType.NotEnoughBalance);
            }

            // Refund the remaining gas to the caller.
            TGasPolicy.UpdateGasUp(ref gas, gasLimitUl);
            if (TTracingInst.IsActive)
            {
                vm.TxTracer.ReportGasUpdateForVmTrace(gasLimitUl, TGasPolicy.GetRemainingGas(in gas));
            }
            return EvmExceptionType.None;
        }

        // Fast-path for zero-value calls: skip snapshot/balance entirely since no state rollback is needed.
        if (transferValue.IsZero && !TTracingInst.IsActive && !vm.TxTracer.IsTracingActions)
        {
            if (codeInfo.IsPrecompile)
            {
                return FastPrecompile(vm, spec, codeInfo, gasLimitUl,
                    ref stack, ref gas, in dataOffset, dataLength, in outputOffset, in outputLength);
            }

            if (codeInfo.Code.Length == 0)
            {
                vm.ReturnDataBuffer = default;
                vm.ReturnData = null;
                stack.PushBytes<OffFlag>(StatusCode.SuccessBytes.Span);
                TGasPolicy.UpdateGasUp(ref gas, gasLimitUl);
                return EvmExceptionType.None;
            }
        }

        // Take a snapshot of the state for potential rollback.
        Snapshot snapshot = state.TakeSnapshot();
        // Subtract the transfer value from the caller's balance.
        state.SubtractFromBalance(caller, in transferValue, spec);

        // Fast-path for non-zero-value calls to externally owned accounts (non-contracts)
        if (codeInfo.Code.Length == 0 && !TTracingInst.IsActive && !vm.TxTracer.IsTracingActions)
        {
            vm.ReturnDataBuffer = default;
            stack.PushBytes<TTracingInst>(StatusCode.SuccessBytes.Span);
            TGasPolicy.UpdateGasUp(ref gas, gasLimitUl);
            return FastCall(vm, spec, in transferValue, target);
        }

        // Precompile fast-path for non-zero value (needs snapshot for rollback)
        if (codeInfo.IsPrecompile && !TTracingInst.IsActive && !vm.TxTracer.IsTracingActions)
        {
            return FastPrecompileWithValue(vm, spec, state, codeInfo, gasLimitUl, in transferValue,
                target, ref stack, ref gas, in dataOffset, dataLength, in outputOffset, in outputLength, in snapshot);
        }

        // Load call data from memory.
        if (!vm.VmState.Memory.TryLoad(in dataOffset, dataLength, out ReadOnlyMemory<byte> callData))
            goto OutOfGas;
        // Construct the execution environment for the call.
        ExecutionEnvironment callEnv = ExecutionEnvironment.Rent(
            codeInfo: codeInfo,
            executingAccount: target,
            caller: caller,
            codeSource: codeSource,
            callDepth: env.CallDepth + 1,
            transferValue: in transferValue,
            value: in callValue,
            inputData: in callData);

        // Normalize output offset if output length is zero.
        if (outputLength == 0)
        {
            // Output offset is inconsequential when output length is 0.
            outputOffset = 0;
        }

        // Rent a new call frame for executing the call.
        vm.ReturnData = VmState<TGasPolicy>.RentFrame(
            gas: TGasPolicy.FromLong(gasLimitUl),
            outputDestination: outputOffset.ToLong(),
            outputLength: outputLength.ToLong(),
            executionType: TOpCall.ExecutionType,
            isStatic: TOpCall.IsStatic || vm.VmState.IsStatic,
            isCreateOnPreExistingAccount: false,
            env: callEnv,
            stateForAccessLists: in vm.VmState.AccessTracker,
            snapshot: in snapshot);

        return EvmExceptionType.DataReturn;

        // Fast-call path for non-contract calls:
        // Directly credit the target account and avoid constructing a full call frame.
        static EvmExceptionType FastCall(VirtualMachine<TGasPolicy> vm, IReleaseSpec spec, in UInt256 transferValue, Address target)
        {
            IWorldState state = vm.WorldState;
            state.AddToBalanceAndCreateIfNotExists(target, transferValue, spec);

            vm.ReturnData = null;
            return EvmExceptionType.None;
        }

        // Fast-path for zero-value precompile calls: no state changes to snapshot/restore.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static EvmExceptionType FastPrecompile(
            VirtualMachine<TGasPolicy> vm, IReleaseSpec spec,
            CodeInfo codeInfo, long gasLimitUl,
            ref EvmStack stack, ref TGasPolicy gas,
            in UInt256 dataOffset, UInt256 dataLength,
            in UInt256 outputOffset, in UInt256 outputLength)
        {
            IPrecompile precompile = codeInfo.Precompile!;

            // Load call data from caller's memory (already expanded/charged).
            if (!vm.VmState.Memory.TryLoad(in dataOffset, dataLength, out ReadOnlyMemory<byte> callData))
            {
                TGasPolicy.UpdateGasUp(ref gas, gasLimitUl);
                vm.ReturnDataBuffer = Array.Empty<byte>();
                stack.PushZero<OffFlag>();
                return EvmExceptionType.None;
            }

            // Compute precompile gas cost.
            long totalGasCost = precompile.BaseGasCost(spec) + precompile.DataGasCost(callData, spec);

            // Check if the precompile has enough gas.
            if ((ulong)totalGasCost > (ulong)gasLimitUl)
            {
                vm.ReturnDataBuffer = Array.Empty<byte>();
                stack.PushZero<OffFlag>();
                vm.ReturnData = null;
                return EvmExceptionType.None;
            }

            // Execute the precompile.
            try
            {
                Result<byte[]> output = precompile.Run(callData, spec);
                if (output)
                {
                    byte[] outputData = output.Data;
                    vm.ReturnDataBuffer = outputData;

                    if (outputLength > 0 && outputData.Length > 0)
                    {
                        int copyLength = Math.Min(outputData.Length, (int)outputLength);
                        vm.VmState.Memory.TrySave(in outputOffset, outputData.AsSpan(0, copyLength).SliceWithZeroPadding(0, copyLength));
                    }

                    stack.PushBytes<OffFlag>(StatusCode.SuccessBytes.Span);
                    TGasPolicy.UpdateGasUp(ref gas, gasLimitUl - totalGasCost);
                    vm.ReturnData = null;
                    return EvmExceptionType.None;
                }
            }
            catch
            {
                // Fall through to failure path
            }

            // Precompile failure: consume all allocated gas, push failure.
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero<OffFlag>();
            vm.ReturnData = null;
            return EvmExceptionType.None;
        }

        // Fast-path for value-transfer precompile calls: needs snapshot for rollback.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static EvmExceptionType FastPrecompileWithValue(
            VirtualMachine<TGasPolicy> vm, IReleaseSpec spec, IWorldState worldState,
            CodeInfo codeInfo, long gasLimitUl, in UInt256 transferValue,
            Address target, ref EvmStack stack, ref TGasPolicy gas,
            in UInt256 dataOffset, UInt256 dataLength,
            in UInt256 outputOffset, in UInt256 outputLength, in Snapshot snapshot)
        {
            IPrecompile precompile = codeInfo.Precompile!;

            if (!vm.VmState.Memory.TryLoad(in dataOffset, dataLength, out ReadOnlyMemory<byte> callData))
            {
                worldState.Restore(snapshot);
                TGasPolicy.UpdateGasUp(ref gas, gasLimitUl);
                vm.ReturnDataBuffer = Array.Empty<byte>();
                stack.PushZero<OffFlag>();
                return EvmExceptionType.None;
            }

            long totalGasCost = precompile.BaseGasCost(spec) + precompile.DataGasCost(callData, spec);

            worldState.AddToBalanceAndCreateIfNotExists(target, transferValue, spec);

            if ((ulong)totalGasCost > (ulong)gasLimitUl)
            {
                worldState.Restore(snapshot);
                vm.ReturnDataBuffer = Array.Empty<byte>();
                stack.PushZero<OffFlag>();
                vm.ReturnData = null;
                return EvmExceptionType.None;
            }

            try
            {
                Result<byte[]> output = precompile.Run(callData, spec);
                if (output)
                {
                    byte[] outputData = output.Data;
                    vm.ReturnDataBuffer = outputData;

                    if (outputLength > 0 && outputData.Length > 0)
                    {
                        int copyLength = Math.Min(outputData.Length, (int)outputLength);
                        vm.VmState.Memory.TrySave(in outputOffset, outputData.AsSpan(0, copyLength).SliceWithZeroPadding(0, copyLength));
                    }

                    stack.PushBytes<OffFlag>(StatusCode.SuccessBytes.Span);
                    TGasPolicy.UpdateGasUp(ref gas, gasLimitUl - totalGasCost);
                    vm.ReturnData = null;
                    return EvmExceptionType.None;
                }
            }
            catch
            {
                // Fall through to failure path
            }

            worldState.Restore(snapshot);
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero<OffFlag>();
            vm.ReturnData = null;
            return EvmExceptionType.None;
        }

    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    }

    private static bool ChargeForLargeContractAccess<TGasPolicy>(uint excessContractSize, Address codeAddress, in StackAccessTracker accessTracer, ref TGasPolicy gas)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        if (accessTracer.WarmUpLargeContract(codeAddress))
        {
            long largeContractCost = GasCostOf.InitCodeWord * EvmCalculations.Div32Ceiling(excessContractSize, out bool outOfGas);
            if (outOfGas || !TGasPolicy.UpdateGas(ref gas, largeContractCost)) return false;
        }

        return true;
    }

    /// <summary>
    /// Executes the RETURN opcode.
    /// Pops a memory offset and a length from the stack, updates memory cost, and sets the return data.
    /// Returns an error if the opcode is executed in an invalid context.
    /// </summary>
    /// <param name="vm">The current virtual machine instance.</param>
    /// <param name="stack">The EVM stack from which the offset and length are popped.</param>
    /// <param name="gas">The gas which is updated by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter (unused in this operation).</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success; otherwise, an error such as <see cref="EvmExceptionType.StackUnderflow"/>,
    /// <see cref="EvmExceptionType.OutOfGas"/>, or <see cref="EvmExceptionType.BadInstruction"/>.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturn<TGasPolicy>(VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        // RETURN is not allowed during contract creation.
        if (vm.VmState.ExecutionType is ExecutionType.EOFCREATE or ExecutionType.TXCREATE)
        {
            goto BadInstruction;
        }

        // Pop memory position and length for the return data.
        if (!stack.PopUInt256(out UInt256 position) ||
            !stack.PopUInt256(out UInt256 length))
            goto StackUnderflow;

        // Update the memory cost for the region being returned.
        if (!TGasPolicy.UpdateMemoryCost(ref gas, in position, in length, vm.VmState) ||
            !vm.VmState.Memory.TryLoad(in position, in length, out ReadOnlyMemory<byte> returnData))
        {
            goto OutOfGas;
        }

        vm.ReturnData = returnData.ToArray();

        return EvmExceptionType.DataReturn;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }
}

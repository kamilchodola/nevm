// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;

namespace Nevm.Benchmarks;

/// <summary>
/// Simple ETH value transfer benchmark (no contract code).
/// Matches gevm's BenchmarkTransfer (21k gas, Cancun).
/// </summary>
[MemoryDiagnoser]
public class TransferBenchmarks : BenchmarkBase
{
    [GlobalSetup]
    public void GlobalSetup()
    {
        SetupVmCancun();
        // Target account exists with some balance (matches gevm)
        StateProvider.CreateAccount(BenchContract, 1_000_000.Wei());
        StateProvider.Commit(Spec);
    }

    [Benchmark]
    public void Transfer()
    {
        // Value transfer with no code — just the EVM entry/exit overhead
        ExecutionEnvironment env = ExecutionEnvironment.Rent(
            executingAccount: BenchContract,
            codeSource: BenchContract,
            caller: BenchCaller,
            codeInfo: new CodeInfo(System.Array.Empty<byte>()),
            callDepth: 0,
            value: 1,
            transferValue: 1,
            inputData: default);

        VmState<EthereumGasPolicy> vmState = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromLong(21_000),
            ExecutionType.TRANSACTION,
            env,
            new StackAccessTracker(),
            StateProvider.TakeSnapshot());

        VirtualMachine.ExecuteTransaction<OffFlag>(vmState, StateProvider, NullTxTracer.Instance);

        vmState.Dispose();
        env.Dispose();
        StateProvider.Reset();
    }
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
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
/// Shared setup and execution helpers for EVM benchmarks.
/// Mirrors gevm's benchmarkCode() / benchmarkNonModifyingCode() patterns.
/// </summary>
public abstract class BenchmarkBase
{
    protected static readonly Address BenchCaller = new("0x0100000000000000000000000000000000000000");
    protected static readonly Address BenchContract = new("0x1000000000000000000000000000000000000000");
    protected static readonly Address BenchEOA = new("0xe000000000000000000000000000000000000000");

    protected IVirtualMachine VirtualMachine = null!;
    protected IWorldState StateProvider = null!;
    protected IReleaseSpec Spec = null!;
    protected BlockHeader Header = null!;
    protected IBlockhashProvider BlockhashProvider = null!;
    private IDisposable _scope = null!;

    protected void SetupVm(IReleaseSpec spec, long blockNumber = 1)
    {
        Spec = spec;
        StateProvider = TestWorldStateFactory.CreateForTest();
        _scope = StateProvider.BeginScope(IWorldState.PreGenesis);
        StateProvider.CreateAccount(BenchCaller, 1_000_000.Ether());
        StateProvider.Commit(spec);

        BlockhashProvider = new TestBlockhashProvider(MainnetSpecProvider.Instance);
        Header = new BlockHeader(Keccak.Zero, Keccak.Zero, Address.Zero, UInt256.One, blockNumber, long.MaxValue, 1UL, []);

        EthereumCodeInfoRepository codeInfoRepository = new(StateProvider);
        VirtualMachine = new EthereumVirtualMachine(BlockhashProvider, MainnetSpecProvider.Instance, LimboLogs.Instance);
        VirtualMachine.SetBlockExecutionContext(new BlockExecutionContext(Header, spec));
        VirtualMachine.SetTxExecutionContext(new TxExecutionContext(BenchCaller, codeInfoRepository, null, 0));
    }

    protected void SetupVmCancun()
    {
        SetupVm(MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.CancunActivation));
    }

    protected void DeployContract(byte[] bytecode)
    {
        StateProvider.CreateAccount(BenchContract, UInt256.Zero);
        StateProvider.InsertCode(BenchContract, bytecode, Spec);
        StateProvider.Commit(Spec);
    }

    protected void SetContractStorage(in UInt256 index, byte[] value)
    {
        StateProvider.Set(new StorageCell(BenchContract, index), value);
    }

    protected void ExecuteCode(byte[] bytecode, long gasLimit, ReadOnlyMemory<byte> inputData = default)
    {
        ExecutionEnvironment env = ExecutionEnvironment.Rent(
            executingAccount: BenchContract,
            codeSource: BenchContract,
            caller: BenchCaller,
            codeInfo: new CodeInfo(bytecode),
            callDepth: 0,
            value: 0,
            transferValue: 0,
            inputData: in inputData);

        VmState<EthereumGasPolicy> vmState = VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromLong(gasLimit),
            ExecutionType.TRANSACTION,
            env,
            new StackAccessTracker(),
            StateProvider.TakeSnapshot());

        VirtualMachine.ExecuteTransaction<OffFlag>(vmState, StateProvider, NullTxTracer.Instance);

        vmState.Dispose();
        env.Dispose();
        StateProvider.Reset();
    }

    protected static byte[] LoadEmbeddedHex(string resourceName)
    {
        Assembly assembly = typeof(BenchmarkBase).Assembly;
        string fullName = $"Nevm.Benchmarks.testdata.{resourceName}";
        using Stream? stream = assembly.GetManifestResourceStream(fullName);
        if (stream is null)
            throw new InvalidOperationException($"Embedded resource '{fullName}' not found.");
        using StreamReader reader = new(stream);
        string hex = reader.ReadToEnd().Trim();
        return Bytes.FromHexString(hex);
    }

    /// <summary>
    /// Builds a tight-loop contract: JUMPDEST [body] PUSH1(0) JUMP
    /// </summary>
    protected static byte[] OpcodeLoop(params byte[] body)
    {
        byte[] code = new byte[1 + body.Length + 3];
        code[0] = 0x5B; // JUMPDEST
        body.CopyTo(code, 1);
        code[1 + body.Length] = 0x60;     // PUSH1
        code[1 + body.Length + 1] = 0x00; // offset 0
        code[1 + body.Length + 2] = 0x56; // JUMP
        return code;
    }

    /// <summary>
    /// Builds a loop with one-time setup: [setup] JUMPDEST [body] PUSH1(jdOffset) JUMP
    /// </summary>
    protected static byte[] OpcodeLoopWithSetup(byte[] setup, params byte[] body)
    {
        int jdOffset = setup.Length;
        byte[] code = new byte[setup.Length + 1 + body.Length + 3];
        setup.CopyTo(code, 0);
        code[jdOffset] = 0x5B; // JUMPDEST
        body.CopyTo(code, jdOffset + 1);
        code[jdOffset + 1 + body.Length] = 0x60;              // PUSH1
        code[jdOffset + 1 + body.Length + 1] = (byte)jdOffset; // jump target
        code[jdOffset + 1 + body.Length + 2] = 0x56;          // JUMP
        return code;
    }
}

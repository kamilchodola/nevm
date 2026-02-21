// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// nevm-evmbench is a runner for the evm-bench benchmarking framework.
// It deploys a contract via CREATE, then times repeated CALL executions.
// With --runtime-code, it skips CREATE and directly injects the bytecode.
// Output format: one line per run with elapsed milliseconds (float).

using System;
using System.Diagnostics;
using System.IO;
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

// Match geth/gevm runner address scheme
Address callerAddress = new("0x1000000000000000000000000000000000000001");
Address contractAddress = new("0x2000000000000000000000000000000000000002");
Address coinbase = Address.Zero;

string? contractCodePath = null;
string calldataHex = "";
int numRuns = 0;
bool isRuntimeCode = false;
long gasLimitOverride = 0;

// Parse arguments (matching --contract-code-path, --calldata, --num-runs, --runtime-code, --gas-limit)
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--contract-code-path":
            contractCodePath = args[++i];
            break;
        case "--calldata":
            calldataHex = args[++i];
            break;
        case "--num-runs":
            numRuns = int.Parse(args[++i]);
            break;
        case "--runtime-code":
            isRuntimeCode = true;
            break;
        case "--gas-limit":
            gasLimitOverride = long.Parse(args[++i]);
            break;
    }
}

if (contractCodePath is null || numRuns == 0)
{
    Console.Error.WriteLine("usage: nevm-evmbench --contract-code-path <path> --calldata <hex> --num-runs <n> [--runtime-code] [--gas-limit <n>]");
    return 1;
}

// Read and decode contract code (hex file)
string contractCodeHex = File.ReadAllText(contractCodePath).Trim();
byte[] contractCode = Bytes.FromHexString(contractCodeHex);
byte[] calldataBytes = calldataHex.Length > 0 ? Bytes.FromHexString(calldataHex) : [];

// Use Cancun spec (matching gevm's runner)
IReleaseSpec spec = MainnetSpecProvider.Instance.GetSpec(MainnetSpecProvider.CancunActivation);
long gasLimit = gasLimitOverride > 0 ? gasLimitOverride : long.MaxValue;

// Set up world state
IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
using IDisposable scope = stateProvider.BeginScope(IWorldState.PreGenesis);

// Fund the caller with a huge balance
stateProvider.CreateAccount(callerAddress, new UInt256(0, 0, 1, 0)); // ~2^128
stateProvider.Commit(spec);

IBlockhashProvider blockhashProvider = new TestBlockhashProvider(MainnetSpecProvider.Instance);
BlockHeader header = new(Keccak.Zero, Keccak.Zero, coinbase, UInt256.One, 1L, gasLimit, 1UL, []);

EthereumCodeInfoRepository codeInfoRepository = new(stateProvider);
IVirtualMachine vm = new EthereumVirtualMachine(blockhashProvider, MainnetSpecProvider.Instance, LimboLogs.Instance);
vm.SetBlockExecutionContext(new BlockExecutionContext(header, spec));
vm.SetTxExecutionContext(new TxExecutionContext(callerAddress, codeInfoRepository, null, 0));

byte[] runtimeBytecode;

if (isRuntimeCode)
{
    // Skip CREATE - directly inject runtime bytecode
    runtimeBytecode = contractCode;
    stateProvider.CreateAccount(contractAddress, UInt256.Zero);
    stateProvider.InsertCode(contractAddress, runtimeBytecode, spec);
    stateProvider.Commit(spec);
}
else
{
    // Step 1: Deploy contract via CREATE - execute init code and capture returned runtime bytecode.
    stateProvider.CreateAccount(contractAddress, UInt256.Zero);
    stateProvider.Commit(spec);

    ReadOnlyMemory<byte> emptyInput = ReadOnlyMemory<byte>.Empty;
    ExecutionEnvironment deployEnv = ExecutionEnvironment.Rent(
        executingAccount: contractAddress,
        codeSource: contractAddress,
        caller: callerAddress,
        codeInfo: new CodeInfo(contractCode),
        callDepth: 0,
        value: 0,
        transferValue: 0,
        inputData: in emptyInput);

    VmState<EthereumGasPolicy> deployState = VmState<EthereumGasPolicy>.RentTopLevel(
        EthereumGasPolicy.FromLong(gasLimit),
        ExecutionType.CREATE,
        deployEnv,
        new StackAccessTracker(),
        stateProvider.TakeSnapshot());

    TransactionSubstate deployResult = vm.ExecuteTransaction<OffFlag>(deployState, stateProvider, NullTxTracer.Instance);

    if (deployResult.IsError || deployResult.ShouldRevert)
    {
        Console.Error.WriteLine($"CREATE failed: {deployResult.Error}");
        deployState.Dispose();
        deployEnv.Dispose();
        return 1;
    }

    runtimeBytecode = deployResult.Output.Bytes.ToArray();
    if (runtimeBytecode.Length == 0)
    {
        Console.Error.WriteLine("CREATE succeeded but returned empty bytecode");
        deployState.Dispose();
        deployEnv.Dispose();
        return 1;
    }

    deployState.Dispose();
    deployEnv.Dispose();

    // Deploy the runtime bytecode at the contract address
    stateProvider.InsertCode(contractAddress, runtimeBytecode, spec);
    stateProvider.Commit(spec);
}

// Step 2: Warmup runs (JIT tiered compilation) — not timed.
// .NET needs multiple invocations for Tier 1 JIT to kick in.
const int warmupIterations = 10;
for (int w = 0; w < warmupIterations; w++)
{
    ReadOnlyMemory<byte> warmupCalldata = calldataBytes;
    ExecutionEnvironment warmupEnv = ExecutionEnvironment.Rent(
        executingAccount: contractAddress,
        codeSource: contractAddress,
        caller: callerAddress,
        codeInfo: new CodeInfo(runtimeBytecode),
        callDepth: 0,
        value: 0,
        transferValue: 0,
        inputData: in warmupCalldata);

    VmState<EthereumGasPolicy> warmupState = VmState<EthereumGasPolicy>.RentTopLevel(
        EthereumGasPolicy.FromLong(gasLimit),
        ExecutionType.TRANSACTION,
        warmupEnv,
        new StackAccessTracker(),
        stateProvider.TakeSnapshot());

    vm.ExecuteTransaction<OffFlag>(warmupState, stateProvider, NullTxTracer.Instance);
    warmupState.Dispose();
    warmupEnv.Dispose();
    stateProvider.Reset();
    stateProvider.Commit(spec);
}

// Step 3: Time CALL executions
ReadOnlyMemory<byte> calldata = calldataBytes;
CodeInfo runtimeCodeInfo = new(runtimeBytecode);

for (int run = 0; run < numRuns; run++)
{
    ExecutionEnvironment callEnv = ExecutionEnvironment.Rent(
        executingAccount: contractAddress,
        codeSource: contractAddress,
        caller: callerAddress,
        codeInfo: runtimeCodeInfo,
        callDepth: 0,
        value: 0,
        transferValue: 0,
        inputData: in calldata);

    VmState<EthereumGasPolicy> callState = VmState<EthereumGasPolicy>.RentTopLevel(
        EthereumGasPolicy.FromLong(gasLimit),
        ExecutionType.TRANSACTION,
        callEnv,
        new StackAccessTracker(),
        stateProvider.TakeSnapshot());

    long startTicks = Stopwatch.GetTimestamp();
    TransactionSubstate callResult = vm.ExecuteTransaction<OffFlag>(callState, stateProvider, NullTxTracer.Instance);
    double elapsedMs = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;

    if (callResult.IsError || callResult.ShouldRevert)
    {
        Console.Error.WriteLine($"CALL failed on run {run}: {callResult.Error}");
    }

    Console.WriteLine(elapsedMs.ToString("F3"));

    callState.Dispose();
    callEnv.Dispose();
    stateProvider.Reset();

    // Re-commit state for next run (reset clears it)
    stateProvider.Commit(spec);
}

return 0;

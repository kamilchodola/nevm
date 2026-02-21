// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;

namespace Nevm.Benchmarks;

/// <summary>
/// SWAP1 throughput and RETURN memory expansion benchmarks.
/// Matches gevm's BenchmarkSWAP1 and BenchmarkRETURN.
/// </summary>
[MemoryDiagnoser]
public class SwapReturnBenchmarks : BenchmarkBase
{
    private const long GasLimit = 10_000_000;

    private static byte[] SwapContract(int count)
    {
        byte[] code = new byte[2 + count];
        code[0] = 0x5F; // PUSH0
        code[1] = 0x5F; // PUSH0
        for (int i = 0; i < count; i++)
        {
            code[2 + i] = 0x90; // SWAP1
        }
        return code;
    }

    private static byte[] ReturnContract(ulong size)
    {
        byte[] code =
        [
            0x67, 0, 0, 0, 0, 0, 0, 0, 0, // PUSH8 <size>
            0x5F, // PUSH0
            0xF3  // RETURN
        ];
        // Write size as big-endian uint64
        for (int i = 0; i < 8; i++)
        {
            code[1 + i] = (byte)(size >> (56 - i * 8));
        }
        return code;
    }

    private static readonly byte[] Swap10k = SwapContract(10_000);
    private static readonly byte[] Return1K = ReturnContract(1_000);
    private static readonly byte[] Return10K = ReturnContract(10_000);
    private static readonly byte[] Return100K = ReturnContract(100_000);
    private static readonly byte[] Return1M = ReturnContract(1_000_000);

    [GlobalSetup]
    public void GlobalSetup()
    {
        SetupVmCancun();
    }

    [Benchmark]
    public void SWAP1_10k()
    {
        DeployContract(Swap10k);
        ExecuteCode(Swap10k, GasLimit);
    }

    [Benchmark]
    public void RETURN_1K()
    {
        DeployContract(Return1K);
        ExecuteCode(Return1K, GasLimit);
    }

    [Benchmark]
    public void RETURN_10K()
    {
        DeployContract(Return10K);
        ExecuteCode(Return10K, GasLimit);
    }

    [Benchmark]
    public void RETURN_100K()
    {
        DeployContract(Return100K);
        ExecuteCode(Return100K, GasLimit);
    }

    [Benchmark]
    public void RETURN_1M()
    {
        DeployContract(Return1M);
        ExecuteCode(Return1M, GasLimit);
    }
}

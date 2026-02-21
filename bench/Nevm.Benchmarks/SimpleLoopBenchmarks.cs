// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;

namespace Nevm.Benchmarks;

/// <summary>
/// Tight loop benchmarks with various call patterns at 100M gas.
/// Matches gevm's BenchmarkSimpleLoop sub-benchmarks.
/// </summary>
[MemoryDiagnoser]
public class SimpleLoopBenchmarks : BenchmarkBase
{
    private const long GasLimit = 100_000_000;

    // JUMPDEST PUSH1(0) PUSH1(0) PUSH1(0) PUSH1(0) PUSH1(4) GAS STATICCALL POP PUSH1(0) JUMP
    private static readonly byte[] StaticCallIdentity =
    [
        0x5B,       // JUMPDEST
        0x60, 0x00, // PUSH1 0 (retSize)
        0x60, 0x00, // PUSH1 0 (retOffset)
        0x60, 0x00, // PUSH1 0 (argsSize)
        0x60, 0x00, // PUSH1 0 (argsOffset)
        0x60, 0x04, // PUSH1 4 (identity precompile)
        0x5A,       // GAS
        0xFA,       // STATICCALL
        0x50,       // POP
        0x60, 0x00, // PUSH1 0
        0x56        // JUMP
    ];

    // JUMPDEST PUSH1(0) PUSH1(0) PUSH1(0) PUSH1(0) PUSH1(0) PUSH1(4) GAS CALL POP PUSH1(0) JUMP
    private static readonly byte[] CallIdentity =
    [
        0x5B,       // JUMPDEST
        0x60, 0x00, // PUSH1 0 (retSize)
        0x60, 0x00, // PUSH1 0 (retOffset)
        0x60, 0x00, // PUSH1 0 (argsSize)
        0x60, 0x00, // PUSH1 0 (argsOffset)
        0x60, 0x00, // PUSH1 0 (value)
        0x60, 0x04, // PUSH1 4 (identity precompile)
        0x5A,       // GAS
        0xF1,       // CALL
        0x50,       // POP
        0x60, 0x00, // PUSH1 0
        0x56        // JUMP
    ];

    // JUMPDEST PUSH1(0) DUP1 DUP1 DUP1 PUSH1(4) GAS POP POP POP POP POP POP PUSH1(0) JUMP
    private static readonly byte[] LoopCode =
    [
        0x5B,       // JUMPDEST
        0x60, 0x00, // PUSH1 0
        0x80,       // DUP1
        0x80,       // DUP1
        0x80,       // DUP1
        0x60, 0x04, // PUSH1 4
        0x5A,       // GAS
        0x50,       // POP
        0x50,       // POP
        0x50,       // POP
        0x50,       // POP
        0x50,       // POP
        0x50,       // POP
        0x60, 0x00, // PUSH1 0
        0x56        // JUMP
    ];

    // JUMPDEST PUSH1(0) PUSH1(0) PUSH1(0) PUSH1(0) PUSH1(0) PUSH1(0xFF) GAS CALL POP PUSH1(0) JUMP
    private static readonly byte[] CallNonExist =
    [
        0x5B,       // JUMPDEST
        0x60, 0x00, // PUSH1 0 (retSize)
        0x60, 0x00, // PUSH1 0 (retOffset)
        0x60, 0x00, // PUSH1 0 (argsSize)
        0x60, 0x00, // PUSH1 0 (argsOffset)
        0x60, 0x00, // PUSH1 0 (value)
        0x60, 0xFF, // PUSH1 0xFF (non-existent address)
        0x5A,       // GAS
        0xF1,       // CALL
        0x50,       // POP
        0x60, 0x00, // PUSH1 0
        0x56        // JUMP
    ];

    [GlobalSetup]
    public void GlobalSetup()
    {
        SetupVmCancun();
    }

    [Benchmark]
    public void StaticCallIdentity100M()
    {
        DeployContract(StaticCallIdentity);
        ExecuteCode(StaticCallIdentity, GasLimit);
    }

    [Benchmark]
    public void CallIdentity100M()
    {
        DeployContract(CallIdentity);
        ExecuteCode(CallIdentity, GasLimit);
    }

    [Benchmark]
    public void Loop100M()
    {
        DeployContract(LoopCode);
        ExecuteCode(LoopCode, GasLimit);
    }

    [Benchmark]
    public void CallNonExist100M()
    {
        DeployContract(CallNonExist);
        ExecuteCode(CallNonExist, GasLimit);
    }
}

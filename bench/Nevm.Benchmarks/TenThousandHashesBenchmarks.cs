// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;

namespace Nevm.Benchmarks;

/// <summary>
/// 10,000 sequential keccak256 hashes benchmark.
/// Matches gevm's BenchmarkTenThousandHashes.
/// </summary>
[MemoryDiagnoser]
public class TenThousandHashesBenchmarks : BenchmarkBase
{
    // Contract bytecode:
    // JUMPDEST            ; 0x00: loop top
    // PUSH1 0x20          ; size = 32
    // PUSH1 0x00          ; offset = 0
    // SHA3                ; keccak256(memory[0:32])
    // PUSH1 0x00          ; offset = 0
    // MSTORE              ; store hash back
    // PUSH1 0x20          ; offset = 32
    // MLOAD               ; load counter
    // PUSH1 0x01          ; 1
    // ADD                 ; counter++
    // DUP1                ; dup counter
    // PUSH1 0x20          ; offset = 32
    // MSTORE              ; store counter
    // PUSH2 0x2710        ; 10000
    // LT                  ; counter < 10000?
    // PUSH1 0x00          ; jump target
    // JUMPI               ; loop if true
    // STOP
    private static readonly byte[] Bytecode = Bytes.FromHexString("5b6020600020600052602051600101806020526127101060005700");

    [GlobalSetup]
    public void GlobalSetup()
    {
        SetupVmCancun();
        DeployContract(Bytecode);
    }

    [Benchmark]
    public void TenThousandHashes()
    {
        ExecuteCode(Bytecode, 10_000_000);
    }
}

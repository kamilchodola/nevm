// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;

namespace Nevm.Benchmarks;

/// <summary>
/// Individual opcode tight-loop benchmarks.
/// Matches gevm's BenchmarkOpcode — each sub-benchmark creates a contract:
/// JUMPDEST [setup+opcode] PUSH1(0) JUMP and runs with 10M gas.
/// </summary>
[MemoryDiagnoser]
public class OpcodeBenchmarks : BenchmarkBase
{
    private const long GasLimit = 10_000_000;

    // Opcode loop bytecodes (matching gevm's exact patterns)
    private static readonly byte[] AddLoop = OpcodeLoop(0x60, 0x01, 0x60, 0x02, 0x01, 0x50);           // PUSH1(1) PUSH1(2) ADD POP
    private static readonly byte[] MulLoop = OpcodeLoop(0x60, 0x03, 0x60, 0x07, 0x02, 0x50);           // PUSH1(3) PUSH1(7) MUL POP
    private static readonly byte[] SubLoop = OpcodeLoop(0x60, 0x02, 0x60, 0x05, 0x03, 0x50);           // PUSH1(2) PUSH1(5) SUB POP
    private static readonly byte[] DivLoop = OpcodeLoop(0x60, 0x02, 0x60, 0x0A, 0x04, 0x50);           // PUSH1(2) PUSH1(10) DIV POP
    private static readonly byte[] ModLoop = OpcodeLoop(0x60, 0x03, 0x60, 0x0A, 0x06, 0x50);           // PUSH1(3) PUSH1(10) MOD POP
    private static readonly byte[] ExpLoop = OpcodeLoop(0x60, 0x0A, 0x60, 0x02, 0x0A, 0x50);           // PUSH1(10) PUSH1(2) EXP POP
    private static readonly byte[] LtLoop = OpcodeLoop(0x60, 0x02, 0x60, 0x01, 0x10, 0x50);            // PUSH1(2) PUSH1(1) LT POP
    private static readonly byte[] EqLoop = OpcodeLoop(0x60, 0x01, 0x60, 0x01, 0x14, 0x50);            // PUSH1(1) PUSH1(1) EQ POP
    private static readonly byte[] IsZeroLoop = OpcodeLoop(0x60, 0x00, 0x15, 0x50);                    // PUSH1(0) ISZERO POP
    private static readonly byte[] AndLoop = OpcodeLoop(0x60, 0xFF, 0x60, 0x0F, 0x16, 0x50);           // PUSH1(0xFF) PUSH1(0x0F) AND POP
    private static readonly byte[] ShlLoop = OpcodeLoop(0x60, 0xFF, 0x60, 0x04, 0x1B, 0x50);           // PUSH1(0xFF) PUSH1(4) SHL POP
    private static readonly byte[] ShrLoop = OpcodeLoop(0x60, 0xFF, 0x60, 0x04, 0x1C, 0x50);           // PUSH1(0xFF) PUSH1(4) SHR POP
    private static readonly byte[] Keccak256Loop = OpcodeLoop(0x60, 0x20, 0x60, 0x00, 0x20, 0x50);     // PUSH1(32) PUSH1(0) KECCAK256 POP
    private static readonly byte[] MloadLoop = OpcodeLoop(0x60, 0x00, 0x51, 0x50);                     // PUSH1(0) MLOAD POP
    private static readonly byte[] MstoreLoop = OpcodeLoop(0x60, 0x2A, 0x60, 0x00, 0x52);              // PUSH1(42) PUSH1(0) MSTORE
    private static readonly byte[] CalldataloadLoop = OpcodeLoop(0x60, 0x00, 0x35, 0x50);              // PUSH1(0) CALLDATALOAD POP
    private static readonly byte[] Push1PopLoop = OpcodeLoop(0x60, 0x01, 0x50);                        // PUSH1(1) POP (baseline dispatch)
    private static readonly byte[] Dup1PopLoop = OpcodeLoopWithSetup([0x60, 0x00], 0x80, 0x50);        // setup: PUSH1(0), loop: DUP1 POP
    private static readonly byte[] Swap1Loop = OpcodeLoopWithSetup([0x60, 0x00, 0x60, 0x00], 0x90);    // setup: PUSH1(0) PUSH1(0), loop: SWAP1

    [GlobalSetup]
    public void GlobalSetup()
    {
        SetupVmCancun();
    }

    [Benchmark] public void ADD() { DeployContract(AddLoop); ExecuteCode(AddLoop, GasLimit); }
    [Benchmark] public void MUL() { DeployContract(MulLoop); ExecuteCode(MulLoop, GasLimit); }
    [Benchmark] public void SUB() { DeployContract(SubLoop); ExecuteCode(SubLoop, GasLimit); }
    [Benchmark] public void DIV() { DeployContract(DivLoop); ExecuteCode(DivLoop, GasLimit); }
    [Benchmark] public void MOD() { DeployContract(ModLoop); ExecuteCode(ModLoop, GasLimit); }
    [Benchmark] public void EXP() { DeployContract(ExpLoop); ExecuteCode(ExpLoop, GasLimit); }
    [Benchmark] public void LT() { DeployContract(LtLoop); ExecuteCode(LtLoop, GasLimit); }
    [Benchmark] public void EQ() { DeployContract(EqLoop); ExecuteCode(EqLoop, GasLimit); }
    [Benchmark] public void ISZERO() { DeployContract(IsZeroLoop); ExecuteCode(IsZeroLoop, GasLimit); }
    [Benchmark] public void AND() { DeployContract(AndLoop); ExecuteCode(AndLoop, GasLimit); }
    [Benchmark] public void SHL() { DeployContract(ShlLoop); ExecuteCode(ShlLoop, GasLimit); }
    [Benchmark] public void SHR() { DeployContract(ShrLoop); ExecuteCode(ShrLoop, GasLimit); }
    [Benchmark] public void KECCAK256() { DeployContract(Keccak256Loop); ExecuteCode(Keccak256Loop, GasLimit); }
    [Benchmark] public void MLOAD() { DeployContract(MloadLoop); ExecuteCode(MloadLoop, GasLimit); }
    [Benchmark] public void MSTORE() { DeployContract(MstoreLoop); ExecuteCode(MstoreLoop, GasLimit); }
    [Benchmark] public void CALLDATALOAD() { DeployContract(CalldataloadLoop); ExecuteCode(CalldataloadLoop, GasLimit); }
    [Benchmark] public void PUSH1_POP() { DeployContract(Push1PopLoop); ExecuteCode(Push1PopLoop, GasLimit); }
    [Benchmark] public void DUP1_POP() { DeployContract(Dup1PopLoop); ExecuteCode(Dup1PopLoop, GasLimit); }
    [Benchmark] public void SWAP1() { DeployContract(Swap1Loop); ExecuteCode(Swap1Loop, GasLimit); }
}

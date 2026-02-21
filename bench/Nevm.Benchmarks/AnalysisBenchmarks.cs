// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;

namespace Nevm.Benchmarks;

/// <summary>
/// ERC-20 deployment code execution benchmark.
/// Matches gevm's BenchmarkAnalysis (selector 0x8035F0CE, 1M gas).
/// </summary>
[MemoryDiagnoser]
public class AnalysisBenchmarks : BenchmarkBase
{
    private byte[] _bytecode = null!;
    private byte[] _calldata = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        SetupVmCancun();
        _bytecode = LoadEmbeddedHex("analysis.hex");
        DeployContract(_bytecode);
        _calldata = Bytes.FromHexString("8035F0CE");
    }

    [Benchmark]
    public void Analysis()
    {
        ExecuteCode(_bytecode, 1_000_000, _calldata);
    }
}

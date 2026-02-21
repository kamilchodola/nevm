// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;

namespace Nevm.Benchmarks;

/// <summary>
/// Compute-heavy ray tracer contract benchmark.
/// Matches gevm's BenchmarkSnailtracer (selector 0x30627b7c, 1B gas).
/// </summary>
[MemoryDiagnoser]
public class SnailtracerBenchmarks : BenchmarkBase
{
    private byte[] _bytecode = null!;
    private byte[] _calldata = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        SetupVmCancun();
        _bytecode = LoadEmbeddedHex("snailtracer.hex");
        DeployContract(_bytecode);
        _calldata = Bytes.FromHexString("30627b7c");
    }

    [Benchmark]
    public void Snailtracer()
    {
        ExecuteCode(_bytecode, 1_000_000_000, _calldata);
    }
}

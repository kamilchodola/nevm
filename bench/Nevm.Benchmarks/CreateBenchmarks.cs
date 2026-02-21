// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;

namespace Nevm.Benchmarks;

/// <summary>
/// Contract creation loop benchmarks.
/// Matches gevm's BenchmarkCREATE_500/1200 and BenchmarkCREATE2_500/1200.
/// Uses Petersburg spec (pre-EIP-3860/2929) to match go-ethereum's config.
/// </summary>
[MemoryDiagnoser]
public class CreateBenchmarks : BenchmarkBase
{
    private const long GasLimit = 10_000_000;

    // Exact bytecodes from gevm
    private static readonly byte[] Create500 = Bytes.FromHexString("5b6207a120600080f0600152600056");
    private static readonly byte[] Create2_500 = Bytes.FromHexString("5b586207a120600080f5600152600056");
    private static readonly byte[] Create1200 = Bytes.FromHexString("5b62124f80600080f0600152600056");
    private static readonly byte[] Create2_1200 = Bytes.FromHexString("5b5862124f80600080f5600152600056");

    [GlobalSetup]
    public void GlobalSetup()
    {
        SetupVm(MainnetSpecProvider.Instance.GetSpec((ForkActivation)MainnetSpecProvider.ConstantinopleFixBlockNumber));
    }

    [Benchmark]
    public void CREATE_500()
    {
        DeployContract(Create500);
        ExecuteCode(Create500, GasLimit);
    }

    [Benchmark]
    public void CREATE2_500()
    {
        DeployContract(Create2_500);
        ExecuteCode(Create2_500, GasLimit);
    }

    [Benchmark]
    public void CREATE_1200()
    {
        DeployContract(Create1200);
        ExecuteCode(Create1200, GasLimit);
    }

    [Benchmark]
    public void CREATE2_1200()
    {
        DeployContract(Create2_1200);
        ExecuteCode(Create2_1200, GasLimit);
    }
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nevm.Benchmarks;

/// <summary>
/// ERC-20 token transfer benchmark with pre-populated storage.
/// Matches gevm's BenchmarkERC20Transfer.
/// </summary>
[MemoryDiagnoser]
public class ERC20Benchmarks : BenchmarkBase
{
    private byte[] _bytecode = null!;
    private byte[] _calldata = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        SetupVmCancun();
        _bytecode = LoadEmbeddedHex("erc20_runtime.hex");
        DeployContract(_bytecode);

        // Pre-populate storage matching gevm:
        // slot 0: totalSupply = large balance
        // keccak256(abi.encode(caller, 1)): balances[caller] = large balance
        UInt256 largeBalance = new(0, 0, 1, 0); // ~2^128
        byte[] balanceBytes = new byte[32];
        largeBalance.ToBigEndian(balanceBytes);

        SetContractStorage(UInt256.Zero, balanceBytes);

        UInt256 callerSlot = SolidityMappingSlot(BenchCaller, 1);
        SetContractStorage(callerSlot, balanceBytes);

        StateProvider.Commit(Spec);

        // ABI: transfer(address to, uint256 amount) selector = 0xa9059cbb
        _calldata = new byte[4 + 32 + 32];
        Bytes.FromHexString("a9059cbb").CopyTo(_calldata, 0);
        // to = BenchEOA (address padded to 32 bytes)
        BenchEOA.Bytes.CopyTo(_calldata.AsSpan(4 + 12));
        // amount = 1
        _calldata[4 + 32 + 31] = 1;
    }

    [Benchmark]
    public void ERC20Transfer()
    {
        ExecuteCode(_bytecode, 100_000, _calldata);
    }

    /// <summary>
    /// Computes keccak256(abi.encode(key, slot)) for a Solidity mapping.
    /// </summary>
    private static UInt256 SolidityMappingSlot(Nethermind.Core.Address key, ulong slot)
    {
        Span<byte> buf = stackalloc byte[64];
        buf.Clear();
        // key padded to 32 bytes (left-padded with zeros, address in last 20 bytes)
        key.Bytes.CopyTo(buf.Slice(12, 20));
        // slot as 32-byte big-endian
        buf[56] = (byte)(slot >> 56);
        buf[57] = (byte)(slot >> 48);
        buf[58] = (byte)(slot >> 40);
        buf[59] = (byte)(slot >> 32);
        buf[60] = (byte)(slot >> 24);
        buf[61] = (byte)(slot >> 16);
        buf[62] = (byte)(slot >> 8);
        buf[63] = (byte)slot;
        Nethermind.Core.Crypto.Hash256 hash = Keccak.Compute(buf);
        return new UInt256(hash.Bytes, isBigEndian: true);
    }
}

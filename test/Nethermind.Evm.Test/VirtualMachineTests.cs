// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;
using Nethermind.Specs;

namespace Nethermind.Evm.Test;

[Parallelizable(ParallelScope.Self)]
public class VirtualMachineTests : VirtualMachineTestsBase
{
    [Test]
    public void Stop()
    {
        TestAllTracerWithOutput receipt = Execute((byte)Instruction.STOP);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction));
    }

    [Test]
    public void Add_0_0()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SReset), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 0 }), "storage");
    }

    [Test]
    public void Add_0_1()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            1,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 1 }), "storage");
    }

    [Test]
    public void Add_1_0()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            1,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.ADD,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 4 * GasCostOf.VeryLow + GasCostOf.SSet), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 1 }), "storage");
    }

    [Test]
    public void Mstore()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            96, // data
            (byte)Instruction.PUSH1,
            64, // position
            (byte)Instruction.MSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Memory * 3), "gas");
    }

    [Test]
    public void Mstore_twice_same_location()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            96,
            (byte)Instruction.PUSH1,
            64,
            (byte)Instruction.MSTORE,
            (byte)Instruction.PUSH1,
            96,
            (byte)Instruction.PUSH1,
            64,
            (byte)Instruction.MSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 6 + GasCostOf.Memory * 3), "gas");
    }

    [Test]
    public void Mload()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            64, // position
            (byte)Instruction.MLOAD);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 2 + GasCostOf.Memory * 3), "gas");
    }

    [Test]
    public void Mload_after_mstore()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            96,
            (byte)Instruction.PUSH1,
            64,
            (byte)Instruction.MSTORE,
            (byte)Instruction.PUSH1,
            64,
            (byte)Instruction.MLOAD);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 5 + GasCostOf.Memory * 3), "gas");
    }

    [Test]
    public void Dup1()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.DUP1);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 2), "gas");
    }

    [Test]
    public void Codecopy()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            32, // length
            (byte)Instruction.PUSH1,
            0, // src
            (byte)Instruction.PUSH1,
            32, // dest
            (byte)Instruction.CODECOPY);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.Memory * 3), "gas");
    }

    [Test]
    public void Swap()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            32, // length
            (byte)Instruction.PUSH1,
            0, // src
            (byte)Instruction.SWAP1);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3), "gas");
    }

    [Test]
    public void Sload()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0, // index
            (byte)Instruction.SLOAD);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 1 + GasCostOf.SLoadEip150), "gas");
    }

    [Test]
    public void Exp_2_160()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            160,
            (byte)Instruction.PUSH1,
            2,
            (byte)Instruction.EXP,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.SSet + GasCostOf.Exp + GasCostOf.ExpByteEip160), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.Pow(2, 160).ToBigEndianByteArray()), "storage");
    }

    [Test]
    public void Exp_0_0()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.EXP,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Exp + GasCostOf.SSet), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.One.ToBigEndianByteArray()), "storage");
    }

    [Test]
    public void Exp_0_160()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            160,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.EXP,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Exp + GasCostOf.ExpByteEip160 + GasCostOf.SReset), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.Zero.ToBigEndianByteArray()), "storage");
    }

    [Test]
    public void Exp_1_160()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            160,
            (byte)Instruction.PUSH1,
            1,
            (byte)Instruction.EXP,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.Exp + GasCostOf.ExpByteEip160 + GasCostOf.SSet), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.One.ToBigEndianByteArray()), "storage");
    }

    [Test]
    public void Sub_0_0()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SUB,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.SReset), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(new byte[] { 0 }), "storage");
    }

    [Test]
    public void Not_0()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.NOT,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 3 + GasCostOf.SSet), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo((BigInteger.Pow(2, 256) - 1).ToBigEndianByteArray()), "storage");
    }

    [Test]
    public void Or_0_0()
    {
        TestAllTracerWithOutput receipt = Execute((MainnetSpecProvider.ByzantiumBlockNumber, null),
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.OR,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 4 + GasCostOf.SReset), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.Zero.ToBigEndianByteArray()), "storage");
    }

    [Test]
    public void Sstore_twice_0_same_storage_should_refund_only_once()
    {
        TestAllTracerWithOutput receipt = Execute(
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.PUSH1,
            0,
            (byte)Instruction.SSTORE);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 2 + GasCostOf.SReset), "gas");
        Assert.That(TestState.Get(new StorageCell(Recipient, 0)).ToArray(), Is.EqualTo(BigInteger.Zero.ToBigEndianByteArray()), "storage");
    }

    /// <summary>
    /// TLoad gas cost check
    /// </summary>
    [Test]
    public void Tload()
    {
        byte[] code = Prepare.EvmCode
            .PushData(96)
            .Op(Instruction.TLOAD)
            .Done;

        TestAllTracerWithOutput receipt = Execute((MainnetSpecProvider.ParisBlockNumber, MainnetSpecProvider.CancunBlockTimestamp), 100000, code);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 1 + GasCostOf.TLoad), "gas");
    }

    /// <summary>
    /// TStore gas cost check
    /// </summary>
    [Test]
    public void Tstore()
    {
        byte[] code = Prepare.EvmCode
            .PushData(96)
            .PushData(64)
            .Op(Instruction.TSTORE)
            .Done;

        TestAllTracerWithOutput receipt = Execute((MainnetSpecProvider.ParisBlockNumber, MainnetSpecProvider.CancunBlockTimestamp), 100000, code);
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 2 + GasCostOf.TStore), "gas");
    }

    [Test]
    public void Revert()
    {
        // See: https://eips.ethereum.org/EIPS/eip-140

        byte[] code = Bytes.FromHexString("0x6c726576657274656420646174616000557f726576657274206d657373616765000000000000000000000000000000000000600052600e6000fd");
        TestAllTracerWithOutput receipt = Execute(blockNumber: MainnetSpecProvider.ByzantiumBlockNumber, 100_000, code);

        Assert.That(receipt.Error, Is.EqualTo("revert message"));
        Assert.That(receipt.GasSpent, Is.EqualTo(GasCostOf.Transaction + 20024));
    }
}

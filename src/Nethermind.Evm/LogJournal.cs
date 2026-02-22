// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm;

/// <summary>
/// Stores log entries compactly during EVM execution, deferring all heap allocations
/// (Hash256, Hash256[], LogEntry, byte[]) to <see cref="ToArray"/> which runs after execution completes.
/// During execution, topics are stored inline as <see cref="ValueHash256"/> and data bytes
/// are appended to a reusable flat buffer — zero per-LOG allocations after warmup.
/// </summary>
public sealed class LogJournal : IToArrayCollection<LogEntry>, IJournal<int>
{
    private struct Entry
    {
        public Address Address;
        public int DataOffset;
        public int DataLength;
        public byte TopicCount;
        public ValueHash256 Topic0;
        public ValueHash256 Topic1;
        public ValueHash256 Topic2;
        public ValueHash256 Topic3;
    }

    private readonly List<Entry> _entries = new();
    private byte[] _dataBuffer = new byte[1024];
    private int _dataPosition;

    public int Count => _entries.Count;

    public void AddEntry(Address address, ReadOnlySpan<byte> data, int topicCount,
        in ValueHash256 topic0 = default, in ValueHash256 topic1 = default,
        in ValueHash256 topic2 = default, in ValueHash256 topic3 = default)
    {
        int required = _dataPosition + data.Length;
        if (required > _dataBuffer.Length)
        {
            int newSize = Math.Max(_dataBuffer.Length * 2, required);
            byte[] newBuffer = new byte[newSize];
            _dataBuffer.AsSpan(0, _dataPosition).CopyTo(newBuffer);
            _dataBuffer = newBuffer;
        }

        data.CopyTo(_dataBuffer.AsSpan(_dataPosition));

        Entry entry = new()
        {
            Address = address,
            DataOffset = _dataPosition,
            DataLength = data.Length,
            TopicCount = (byte)topicCount,
            Topic0 = topic0,
            Topic1 = topic1,
            Topic2 = topic2,
            Topic3 = topic3,
        };

        _dataPosition += data.Length;
        _entries.Add(entry);
    }

    public int TakeSnapshot() => Count - 1;

    public void Restore(int snapshot)
    {
        if (snapshot >= Count)
            throw new InvalidOperationException(
                $"{nameof(LogJournal)} tried to restore snapshot {snapshot} beyond current position {Count}");

        int newCount = snapshot + 1;
        if (newCount < _entries.Count)
        {
            if (newCount > 0)
            {
                ref Entry last = ref CollectionsMarshal.AsSpan(_entries)[newCount - 1];
                _dataPosition = last.DataOffset + last.DataLength;
            }
            else
            {
                _dataPosition = 0;
            }

            CollectionsMarshal.SetCount(_entries, newCount);
        }
    }

    public LogEntry[] ToArray()
    {
        int count = _entries.Count;
        if (count == 0) return [];

        LogEntry[] result = new LogEntry[count];
        ReadOnlySpan<Entry> span = CollectionsMarshal.AsSpan(_entries);
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly Entry entry = ref span[i];
            result[i] = MaterializeEntry(in entry);
        }

        return result;
    }

    /// <summary>
    /// Materializes a single log entry at the given index. Used for the tracing path.
    /// </summary>
    public LogEntry MaterializeEntry(int index)
    {
        ref readonly Entry entry = ref CollectionsMarshal.AsSpan(_entries)[index];
        return MaterializeEntry(in entry);
    }

    private LogEntry MaterializeEntry(in Entry entry)
    {
        Hash256[] topics = entry.TopicCount switch
        {
            0 => [],
            1 => [new Hash256(entry.Topic0)],
            2 => [new Hash256(entry.Topic0), new Hash256(entry.Topic1)],
            3 => [new Hash256(entry.Topic0), new Hash256(entry.Topic1), new Hash256(entry.Topic2)],
            4 => [new Hash256(entry.Topic0), new Hash256(entry.Topic1), new Hash256(entry.Topic2), new Hash256(entry.Topic3)],
            _ => throw new InvalidOperationException()
        };

        byte[] data = entry.DataLength > 0
            ? _dataBuffer.AsSpan(entry.DataOffset, entry.DataLength).ToArray()
            : [];

        return new LogEntry(entry.Address, data, topics);
    }

    public void Clear()
    {
        _entries.Clear();
        _dataPosition = 0;
    }

    public IEnumerator<LogEntry> GetEnumerator()
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            yield return MaterializeEntry(i);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

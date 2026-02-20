// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.Core.Test.Db;

/// <summary>
/// Simplified test-only IDbProvider backed by MemDb instances.
/// Does not require Autofac/DI container - standalone for nevm.
/// </summary>
public class TestMemDbProvider : IDbProvider
{
    private readonly Dictionary<string, IDb> _dbs = new();

    public static IDbProvider Init()
    {
        return new TestMemDbProvider();
    }

    public T GetDb<T>(string dbName) where T : class, IDb
    {
        if (!_dbs.TryGetValue(dbName, out IDb? db))
        {
            db = new MemDb(dbName);
            _dbs[dbName] = db;
        }

        return (T)db;
    }

    public IColumnsDb<T> GetColumnDb<T>(string dbName)
    {
        return new SimpleColumnsDb<T>();
    }

    public void Dispose()
    {
        foreach (IDb db in _dbs.Values)
        {
            db.Dispose();
        }

        _dbs.Clear();
    }

    private sealed class SimpleColumnsDb<T> : IColumnsDb<T>
    {
        private readonly MemDb _db = new();

        public IDb GetColumnDb(T key) => _db;
        public IEnumerable<T> ColumnKeys => [];

        public IColumnsWriteBatch<T> StartWriteBatch() => new SimpleColumnsWriteBatch<T>(_db);
        public IColumnDbSnapshot<T> CreateSnapshot() => new SimpleColumnDbSnapshot<T>(_db);

        public void Flush(bool onlyWal = false) => _db.Flush();
        public void Clear() => _db.Clear();
        public void Compact() { }

        public void Dispose() => _db.Dispose();
    }

    private sealed class SimpleColumnsWriteBatch<T>(MemDb db) : IColumnsWriteBatch<T>
    {
        private readonly IWriteBatch _batch = db.StartWriteBatch();

        public IWriteBatch GetColumnBatch(T key) => _batch;
        public void Clear() { }
        public void Dispose() => _batch.Dispose();
    }

    private sealed class SimpleColumnDbSnapshot<T>(MemDb db) : IColumnDbSnapshot<T>
    {
        public IReadOnlyKeyValueStore GetColumn(T key) => db;
        public void Dispose() { }
    }
}

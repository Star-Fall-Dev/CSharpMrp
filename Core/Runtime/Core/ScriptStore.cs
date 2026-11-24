using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace CSharpMrp
{
    // MRP data-only store wire format: LE int32 tags, references and integer numbers.
    // Executable objects, permanent substitutions and metatables are unsupported.
    public static class ScriptStore
    {
        internal const int MaxBytes = 4 * 1024 * 1024;
        internal const int MaxPooledBytes = 512 * 1024;
        private const int MaxReferences = 100000;
        private const int MinimumCachedRootEntries = 128;
        private const int MaximumCachedReferences = 16384;
        private static readonly WeakReference<Dictionary<object, int>> EncoderReferences =
            new WeakReference<Dictionary<object, int>>(null!);
        private static readonly WeakByteBuffer LargeScriptInputBuffer = new WeakByteBuffer();

        public static byte[] Encode(object? root)
        {
            using var encoded = EncodeBuffer(root, transferOwnership: true);
            return encoded.GetOwnedBytes();
        }

        internal static EncodedBuffer EncodeBuffer(object? root, bool transferOwnership = false)
        {
            var writer = new EncodedBuffer(transferOwnership);
            bool cacheReferences = root is ScriptTable rootTable &&
                rootTable.Entries.Count >= MinimumCachedRootEntries && rootTable.Entries.Count < MaximumCachedReferences;
            var references = cacheReferences ? RentEncoderReferences() : new Dictionary<object, int>();
            void WriteStringHeader(int id, int length)
            {
                if (length > MaxBytes - writer.Length - 4 * sizeof(int))
                {
                    throw new RuntimeFault("store size limit");
                }
                writer.WriteObject(id, 4, length);
            }
            void Write(object? value, int depth)
            {
                if (depth > 64 || writer.Length > MaxBytes - 32)
                {
                    throw new RuntimeFault("store size or depth limit");
                }
                int id = 0;
                if (value == null || references.TryGetValue(value, out id))
                {
                    writer.WriteReference(id);
                    return;
                }
                if (references.Count >= MaxReferences)
                {
                    throw new RuntimeFault("store reference limit");
                }
                id = references.Count + 1;
                references.Add(value, id);
                switch (value)
                {
                    case ScriptBoolean _:
                        writer.WriteObject(id, 1, 1);
                        break;
                    case bool boolean:
                        writer.WriteObject(id, 1, boolean ? 1 : 0);
                        break;
                    case int number:
                        writer.WriteObject(id, 3, number);
                        break;
                    case string text:
                        int length = ScriptChunk.Bytes.GetByteCount(text);
                        WriteStringHeader(id, length);
                        ScriptChunk.Bytes.GetBytes(text.AsSpan(), writer.Reserve(length));
                        break;
                    case ScriptMutableString mutable:
                        WriteStringHeader(id, mutable.Length);
                        mutable.Memory.ReadSpan(mutable.Pointer, (uint)mutable.Length).CopyTo(writer.Reserve(mutable.Length));
                        break;
                    case ScriptTable table:
                        writer.WriteObject(id, 5, 0); // literal table
                        Write(null, depth + 1); // no metatable
                        foreach (var item in table.Entries)
                        {
                            Write(item.Key, depth + 1);
                            Write(item.Value, depth + 1);
                        }
                        Write(null, depth + 1); // end of key/value pairs
                        break;
                    default:
                        throw new RuntimeFault("unsupported store value type");
                }
            }
            try
            {
                Write(root, 0);
                return writer;
            }
            catch
            {
                writer.Dispose();
                throw;
            }
            finally
            {
                if (cacheReferences && references.Count <= MaximumCachedReferences)
                {
                    references.Clear();
                    lock (EncoderReferences) EncoderReferences.SetTarget(references);
                }
            }
        }

        private static Dictionary<object, int> RentEncoderReferences()
        {
            lock (EncoderReferences)
            {
                if (EncoderReferences.TryGetTarget(out Dictionary<object, int>? references))
                {
                    EncoderReferences.SetTarget(null!);
                    return references;
                }
            }
            return new Dictionary<object, int>();
        }

        internal sealed class EncodedBuffer : IDisposable
        {
            private static readonly WeakByteBuffer LargeSpare = new WeakByteBuffer();
            private readonly bool transferOwnership;
            private readonly int maximumSize;
            private bool rented;

            internal byte[] Bytes { get; private set; } = Array.Empty<byte>();
            internal int Length { get; private set; }

            internal EncodedBuffer(bool transferOwnership, int maximumSize = MaxBytes)
            {
                this.transferOwnership = transferOwnership;
                this.maximumSize = maximumSize;
            }

            internal byte[] GetOwnedBytes() =>
                transferOwnership && !rented && Bytes.Length == Length ? Bytes : Bytes.AsSpan(0, Length).ToArray();

            internal void WriteReference(int id)
            {
                Span<byte> record = Reserve(2 * sizeof(int));
                BinaryPrimitives.WriteInt32LittleEndian(record, 0);
                BinaryPrimitives.WriteInt32LittleEndian(record.Slice(sizeof(int)), id);
            }

            internal void WriteObject(int id, int type, int data)
            {
                Span<byte> record = Reserve(4 * sizeof(int));
                BinaryPrimitives.WriteInt32LittleEndian(record, 1);
                BinaryPrimitives.WriteInt32LittleEndian(record.Slice(sizeof(int)), id);
                BinaryPrimitives.WriteInt32LittleEndian(record.Slice(2 * sizeof(int)), type);
                BinaryPrimitives.WriteInt32LittleEndian(record.Slice(3 * sizeof(int)), data);
            }

            internal Span<byte> Reserve(int count)
            {
                int offset = Length;
                int required = offset + count;
                if (required > Bytes.Length)
                {
                    int capacity = Math.Max(required, Math.Max(256, Math.Min(maximumSize, Bytes.Length * 2)));
                    bool pool = capacity <= MaxPooledBytes;
                    byte[] next;
                    if (pool) next = ArrayPool<byte>.Shared.Rent(capacity);
                    else if (transferOwnership) next = new byte[capacity];
                    else next = LargeSpare.Rent(capacity, Math.Min(maximumSize, capacity * 2));
                    Bytes.AsSpan(0, offset).CopyTo(next);
                    ReturnRental();
                    Bytes = next;
                    rented = pool;
                }
                Length = required;
                return Bytes.AsSpan(offset, count);
            }

            private void ReturnRental()
            {
                if (!rented && (transferOwnership || Bytes.Length <= MaxPooledBytes)) return;
                Bytes.AsSpan(0, Length).Clear();
                if (rented)
                {
                    ArrayPool<byte>.Shared.Return(Bytes);
                    rented = false;
                }
                else
                {
                    LargeSpare.Return(Bytes);
                }
            }

            public void Dispose()
            {
                ReturnRental();
                Bytes = Array.Empty<byte>();
                Length = 0;
            }
        }

        public static object? Decode(byte[] bytes) => Decode(bytes.AsSpan(0, bytes.Length));

        internal static object? DecodeScriptValue(object? input)
        {
            if (input is ScriptMutableString mutable)
            {
                return Decode(mutable.Memory.ReadSpan(mutable.Pointer, (uint)mutable.Length));
            }

            string text = ScriptVm.Text(input);
            int length = ScriptChunk.Bytes.GetByteCount(text);
            if (length > MaxBytes)
            {
                throw new RuntimeFault("store size limit");
            }

            byte[]? rented = null;
            bool pool = length <= MaxPooledBytes;
            Span<byte> bytes = length <= 256 ? stackalloc byte[length] : (rented = pool
                ? ArrayPool<byte>.Shared.Rent(length)
                : LargeScriptInputBuffer.Rent(length, Math.Min(MaxBytes, length * 2))).AsSpan(0, length);
            try
            {
                ScriptChunk.Bytes.GetBytes(text.AsSpan(), bytes);
                return Decode(bytes);
            }
            finally
            {
                if (rented != null)
                {
                    bytes.Clear();
                    if (pool) ArrayPool<byte>.Shared.Return(rented);
                    else LargeScriptInputBuffer.Return(rented);
                }
            }
        }

        internal static object? Decode(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > MaxBytes)
            {
                throw new RuntimeFault("store size limit");
            }

            var decoder = new Decoder(bytes);
            try { return decoder.ReadRoot(); }
            finally { decoder.ReturnDenseReferences(); }
        }

        private ref struct Decoder
        {
            private const int MaxPooledReferences = 8192;
            private const int IntegerRecordBytes = 4 * sizeof(int);
            private const int IntegerPairBytes = 2 * IntegerRecordBytes;
            private const int MinimumIntegerPrefix = 128;
            private static readonly ArrayPool<object?> ReferencePool = ArrayPool<object?>.Create(MaxPooledReferences, 1);
            private ReadOnlySpan<byte> remaining;
            private object?[] denseReferences;
            private Dictionary<int, object>? sparseReferences;
            private int denseCount;
            private int referenceCount;
            private int nodes;

            internal Decoder(ReadOnlySpan<byte> bytes)
            {
                remaining = bytes;
                denseReferences = Array.Empty<object?>();
                sparseReferences = null;
                denseCount = 0;
                referenceCount = 0;
                nodes = 0;
            }

            private bool TryGetReference(int id, out object? value)
            {
                if (id > 0 && id <= denseCount)
                {
                    value = denseReferences[id - 1];
                    return true;
                }
                if (sparseReferences != null) return sparseReferences.TryGetValue(id, out value);
                value = null;
                return false;
            }

            private void AddReference(int id, object value)
            {
                // The encoder emits consecutive IDs. Keep the existing dense prefix if a file jumps ahead.
                if (sparseReferences == null && id == denseCount + 1)
                {
                    if (denseCount == denseReferences.Length)
                    {
                        int capacity = Math.Min(MaxReferences, Math.Max(16, denseCount * 2));
                        object?[] expanded = capacity <= MaxPooledReferences ? ReferencePool.Rent(capacity) : new object?[capacity];
                        denseReferences.AsSpan(0, denseCount).CopyTo(expanded);
                        ReturnDenseReferences();
                        denseReferences = expanded;
                    }
                    denseReferences[denseCount++] = value;
                }
                else
                {
                    (sparseReferences ??= new Dictionary<int, object>()).Add(id, value);
                }
                referenceCount++;
            }

            internal void ReturnDenseReferences()
            {
                if (denseReferences.Length > 0 && denseReferences.Length <= MaxPooledReferences)
                {
                    ReferencePool.Return(denseReferences, clearArray: true);
                }
            }

            internal object? ReadRoot()
            {
                object? result = Read(0);
                if (!remaining.IsEmpty)
                {
                    throw new RuntimeFault("trailing store data");
                }
                return result;
            }

            private int ReadInt32()
            {
                if (!BinaryPrimitives.TryReadInt32LittleEndian(remaining, out int value))
                {
                    throw new RuntimeFault("truncated store data");
                }
                remaining = remaining.Slice(sizeof(int));
                return value;
            }

            private void ReserveIntegerPrefix(ScriptTable table)
            {
                int limit = Math.Min(remaining.Length / IntegerPairBytes, (MaxReferences - denseCount) / 2);
                if (limit < MinimumIntegerPrefix || sparseReferences != null) return;

                int pairs = 0;
                int nextId = denseCount + 1;
                ReadOnlySpan<byte> input = remaining;
                while (pairs < limit && IsFreshInteger(input, nextId) &&
                    IsFreshInteger(input.Slice(IntegerRecordBytes), nextId + 1))
                {
                    pairs++;
                    nextId += 2;
                    input = input.Slice(IntegerPairBytes);
                }
                if (pairs >= MinimumIntegerPrefix) table.ReserveDecodedEntries(pairs);
            }

            private static bool IsFreshInteger(ReadOnlySpan<byte> bytes, int id) =>
                BinaryPrimitives.ReadInt32LittleEndian(bytes) == 1 &&
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(sizeof(int))) == id &&
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(2 * sizeof(int))) == 3;

            private object? Read(int depth)
            {
                if (depth > 64 || ++nodes > 200000)
                {
                    throw new RuntimeFault("store depth or node limit");
                }
                int fresh = ReadInt32();
                int id = ReadInt32();
                if (fresh == 0)
                {
                    if (id == 0)
                    {
                        return null;
                    }
                    return TryGetReference(id, out object? reference) ? reference
                        : throw new RuntimeFault("invalid store reference");
                }
                if (fresh != 1 || id <= 0 || TryGetReference(id, out _) || referenceCount >= MaxReferences)
                {
                    throw new RuntimeFault("invalid store object header");
                }
                int type = ReadInt32();
                object value;
                switch (type)
                {
                    case 1:
                        int boolean = ReadInt32();
                        value = boolean == 0 ? false : boolean == 1 ? true
                            : throw new RuntimeFault("invalid store boolean");
                        break;
                    case 3:
                        value = ReadInt32();
                        break;
                    case 4:
                        int length = ReadInt32();
                        if (length < 0 || length > remaining.Length)
                        {
                            throw new RuntimeFault("invalid store string length");
                        }
                        value = ScriptChunk.Bytes.GetString(remaining.Slice(0, length));
                        remaining = remaining.Slice(length);
                        break;
                    case 5:
                        var table = new ScriptTable();
                        AddReference(id, table);
                        if (ReadInt32() != 0 || Read(depth + 1) != null)
                        {
                            throw new RuntimeFault("unsupported store special table or metatable");
                        }
                        if (remaining.Length >= MinimumIntegerPrefix * IntegerPairBytes &&
                            BinaryPrimitives.ReadInt32LittleEndian(remaining.Slice(2 * sizeof(int))) == 3)
                            ReserveIntegerPrefix(table);
                        object? key;
                        while ((key = Read(depth + 1)) != null)
                        {
                            table[key] = Read(depth + 1);
                        }
                        return table;
                    default:
                        throw new RuntimeFault($"unsupported store type {type}");
                }
                AddReference(id, value);
                return value;
            }
        }
    }
}

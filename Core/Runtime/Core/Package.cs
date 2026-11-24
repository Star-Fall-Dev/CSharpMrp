using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace CSharpMrp
{
    public sealed class RuntimeFault : Exception
    {
        public RuntimeFault(string message) : base(message)
        {
        }
    }

    // Keep a single spare available between collections without rooting the array.
    internal sealed class WeakByteBuffer
    {
        private readonly WeakReference<byte[]> spare = new WeakReference<byte[]>(null!);

        internal byte[] Rent(int capacity) => Rent(capacity, capacity);

        internal byte[] Rent(int capacity, int maximumCapacity)
        {
            lock (spare)
            {
                if (spare.TryGetTarget(out byte[]? buffer) && buffer.Length >= capacity && buffer.Length <= maximumCapacity)
                {
                    spare.SetTarget(null!);
                    return buffer;
                }
            }
            return new byte[capacity];
        }

        internal void Return(byte[] buffer)
        {
            lock (spare) spare.SetTarget(buffer);
        }
    }

    public sealed class Package
    {
        public const int MaxBytes = 32 * 1024 * 1024;
        private const int ReferenceHistoryBytes = 32768;
        private readonly byte[] bytes;
        private readonly int payloadStart;
        private readonly uint declaredLength = uint.MaxValue;
        private readonly Dictionary<string, (int Offset, int Size)> entries =
            new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        private RuntimeFault? indexFault;
        private readonly List<string> compatibilityDiagnostics = new List<string>();

        public IEnumerable<string> Names => indexFault == null ? entries.Keys : throw indexFault;
        public int Length => bytes.Length;
        internal ReadOnlySpan<byte> Image => bytes;
        public IReadOnlyList<string> CompatibilityDiagnostics => compatibilityDiagnostics;

        public Stream OpenImage()
        {
            return new MemoryStream(bytes, false);
        }

        public Package(byte[] source, bool sequential = false) : this(source, sequential, false) { }

        internal static Package FromOwnedBytes(byte[] source, bool sequential = false) =>
            new Package(source, sequential, true);

        private Package(byte[] source, bool sequential, bool takeOwnership)
        {
            ValidateHeader(source);
            bytes = takeOwnership ? source : (byte[])source.Clone();
            uint headerLength = Word(4);
            // Disk packages with the original short header store consecutive records too.
            if (sequential || headerLength <= 232)
            {
                // The old disk reader scans the physical file; RAM packages use the
                // external length, and registered packages were already sliced by it.
                ParseSequential(SequentialStart(headerLength, bytes.Length));
                return;
            }
            ulong indexEnd = (ulong)headerLength + 8;
            uint start = Word(12);
            declaredLength = Word(8);
            if (start < 16 || start > indexEnd || indexEnd > (ulong)bytes.Length)
            {
                throw new RuntimeFault("package: invalid file length or index range");
            }
            payloadStart = (int)indexEnd;
            int cursor = (int)start;
            while ((ulong)cursor < indexEnd)
            {
                try
                {
                    Range(cursor, 4, (int)indexEnd);
                    uint length = Word(cursor);
                    cursor += 4;
                    if (length < 1 || length >= 128)
                    {
                        throw new RuntimeFault("package: invalid entry name length");
                    }
                    Range(cursor, (int)length + 12, (int)indexEnd);
                    string name = EntryName(cursor, (int)length);
                    cursor += (int)length;
                    uint offset = Word(cursor);
                    uint size = Word(cursor + 4);
                    cursor += 12;
                    // MRP lookup uses the first matching name; some real packages repeat entries.
                    entries.TryAdd(name, ((int)offset, (int)size));
                }
                catch (RuntimeFault error) when (entries.Count != 0)
                {
                    indexFault = error;
                    break;
                }
            }
        }

        private Package(byte[] source, string rawEntry)
        {
            if (source.Length > MaxBytes)
            {
                throw new RuntimeFault("compressed input exceeds size limit");
            }
            bytes = source;
            entries.Add(rawEntry, (0, source.Length));
        }

        internal static byte[] ReadPackedBytes(byte[] source, Action<string>? diagnostic = null)
        {
            return new Package(source, "input").Read("input", diagnostic: diagnostic);
        }

        private void ParseSequential(int cursor)
        {
            while (cursor < bytes.Length)
            {
                try
                {
                    var entry = ReadSequentialEntry(bytes, cursor, out var name);
                    entries.TryAdd(ScriptChunk.Bytes.GetString(name), entry);
                    cursor = entry.Offset + entry.Size;
                }
                catch (RuntimeFault error) when (entries.Count != 0)
                {
                    indexFault = error;
                    break;
                }
            }
        }

        internal static bool TryFindSequentialEntry(ReadOnlySpan<byte> image, string resource, out (int Offset, int Size) entry)
        {
            ValidateHeader(image);
            int cursor = SequentialStart(Word(image, 4), image.Length);
            (int Offset, int Size)? alias = null;
            while (cursor < image.Length)
            {
                try
                {
                    var candidate = ReadSequentialEntry(image, cursor, out var name);
                    if (ScriptChunk.BytesEqualText(name, resource.AsSpan()))
                    {
                        entry = candidate;
                        return true;
                    }
                    if (resource == "update" && !alias.HasValue && ScriptChunk.BytesEqualText(name, "updata".AsSpan()))
                    {
                        alias = candidate;
                    }
                    cursor = candidate.Offset + candidate.Size;
                }
                catch (RuntimeFault) when (alias.HasValue)
                {
                    // A usable alias survives a malformed tail, just as it does in the index.
                    break;
                }
            }
            entry = alias.GetValueOrDefault();
            return alias.HasValue;
        }

        private static (int Offset, int Size) ReadSequentialEntry(ReadOnlySpan<byte> image, int cursor, out ReadOnlySpan<byte> name)
        {
            uint nameSize = Word(image, cursor);
            cursor += 4;
            if (nameSize < 1 || nameSize >= 128)
            {
                throw new RuntimeFault("package: invalid sequential name size");
            }
            Range(cursor, (int)nameSize + 4, image.Length);
            name = ReadEntryName(image.Slice(cursor, (int)nameSize));
            cursor += (int)nameSize;
            uint size = Word(image, cursor);
            cursor += 4;
            if ((ulong)cursor + size > (ulong)image.Length)
            {
                throw new RuntimeFault($"package: sequential payload range for {ScriptChunk.Bytes.GetString(name)}");
            }
            return (cursor, (int)size);
        }

        private static void ValidateHeader(ReadOnlySpan<byte> image)
        {
            if (image.Length < 16 || image.Length > MaxBytes ||
                image[0] != 'M' || image[1] != 'R' || image[2] != 'P' || image[3] != 'G')
            {
                throw new RuntimeFault("package: invalid MRPG header or size");
            }
        }

        private static int SequentialStart(uint headerLength, int imageLength)
        {
            ulong start = (ulong)headerLength + 8;
            if (start < 12 || start > (ulong)imageLength)
            {
                throw new RuntimeFault("package: invalid sequential header");
            }
            return (int)start;
        }

        internal bool HasIndexedEntry(string name) => TryGetIndexedEntry(name, out _, ignoreIndexFault: true);

        public bool Contains(string name) => TryGetIndexedEntry(name, out _);

        internal bool TryGetIndexedEntry(string name, out (int Offset, int Size) entry, bool ignoreIndexFault = false)
        {
            if (entries.TryGetValue(name, out entry) || (name == "update" && entries.TryGetValue("updata", out entry)))
            {
                return true;
            }
            if (!ignoreIndexFault && indexFault != null)
            {
                throw indexFault;
            }
            return false;
        }

        internal void ValidateStoredRange(string name, (int Offset, int Size) entry)
        {
            if (entry.Offset < payloadStart ||
                (ulong)entry.Offset + (uint)entry.Size > (ulong)bytes.Length ||
                (ulong)entry.Offset + (uint)entry.Size > declaredLength)
            {
                throw new RuntimeFault($"package: payload range for {name}");
            }
        }

        public byte[] Read(string name, int limit = MaxBytes, Action<string>? diagnostic = null)
        {
            if (limit < 0 || limit > MaxBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(limit));
            }
            if (!TryGetIndexedEntry(name, out var entry))
            {
                throw new RuntimeFault($"package: missing resource {name}");
            }
            ValidateStoredRange(name, entry);
            int dataOffset = CompressionOffset(name, entry);
            if (dataOffset == 0)
            {
                if (entry.Size > limit)
                {
                    throw new RuntimeFault($"package: {name} exceeds resource limit");
                }
                var result = new byte[entry.Size];
                Array.Copy(bytes, entry.Offset, result, 0, result.Length);
                return result;
            }
            using var output = new InflateBuffer(transferOwnership: true);
            ReadCompressedEntry(name, entry, dataOffset, limit, output, out _, diagnostic);
            return output.TakeBytes();
        }

        private bool IsZipEntry((int Offset, int Size) entry) =>
            entry.Size >= 4 && Word(entry.Offset) == 0x04034b50;

        internal int CompressionOffset(string name, (int Offset, int Size) entry)
        {
            int start = entry.Offset;
            int end = start + entry.Size;
            if (IsZipEntry(entry))
            {
                if (entry.Size < 30)
                {
                    throw new RuntimeFault($"package: invalid ZIP {name}: truncated record");
                }
                int offset = 30 + (bytes[start + 26] | bytes[start + 27] << 8) +
                    (bytes[start + 28] | bytes[start + 29] << 8);
                if (offset > entry.Size)
                {
                    throw new RuntimeFault($"package: invalid ZIP {name}: truncated record");
                }
                return offset;
            }
            if (entry.Size < 2 || bytes[start] != 0x1f ||
                (bytes[start + 1] != 0x8b && bytes[start + 1] != 0x9e))
            {
                return 0;
            }
            int cursor = start + 2;
            RuntimeFault TruncatedHeader() => new RuntimeFault($"package: invalid gzip {name}: truncated header");
            byte Next()
            {
                if (cursor == end)
                {
                    throw TruncatedHeader();
                }
                return bytes[cursor++];
            }
            if (Next() != 8)
            {
                throw new RuntimeFault($"package: invalid gzip {name}: unsupported method");
            }
            int flags = Next();
            if ((flags & 0xe2) != 0)
            {
                throw new RuntimeFault($"package: invalid gzip {name}: unsupported flags");
            }
            if (end - cursor < 6) throw TruncatedHeader();
            cursor += 6;
            if ((flags & 4) != 0)
            {
                int length = Next() | Next() << 8;
                if (length > end - cursor) throw TruncatedHeader();
                cursor += length;
            }
            for (int flag = 8; flag <= 16; flag <<= 1)
            {
                if ((flags & flag) != 0)
                {
                    int terminator = Array.IndexOf(bytes, (byte)0, cursor, end - cursor);
                    if (terminator < 0) throw TruncatedHeader();
                    cursor = terminator + 1;
                }
            }
            return cursor - start;
        }

        internal uint ReadCompressedEntry(string name, (int Offset, int Size) entry, int dataOffset, int limit,
            InflateBuffer output, out int consumed, Action<string>? diagnostic)
        {
            consumed = 2;
            if (IsZipEntry(entry))
            {
                return ReadZip(name, entry.Offset, entry.Size, dataOffset, limit, output, out consumed, diagnostic);
            }
            try
            {
                if (entry.Size < 18)
                {
                    throw new RuntimeFault($"package: invalid gzip {name}: truncated member");
                }
                using var input = new ZipInput(bytes, entry.Offset + dataOffset, entry.Size - dataOffset);
                if (!Inflate(input, output, limit, Word(entry.Offset + entry.Size - 4)))
                {
                    throw new RuntimeFault($"package: {name} exceeds decompression limit");
                }
                uint crc = Crc32.Update(uint.MaxValue, output.Bytes);
                consumed = checked(dataOffset + (int)input.Position + 8);
                if (consumed == entry.Size + 1 && Word(entry.Offset + entry.Size - 4) == output.Length &&
                    Word(entry.Offset + entry.Size - 8) == ~crc)
                {
                    // Unity's Mono inflater can read one trailer byte when an
                    // output block fills exactly. Accept only a fully verified member.
                    string message = $"compatibility gzip trailer read-ahead: {name}; one byte, exact length and CRC verified";
                    if (!compatibilityDiagnostics.Contains(message))
                    {
                        compatibilityDiagnostics.Add(message);
                    }
                    diagnostic?.Invoke(message);
                    consumed = entry.Size;
                }
                if (consumed != entry.Size || Word(entry.Offset + consumed - 4) != output.Length)
                {
                    throw new RuntimeFault($"package: invalid gzip {name}: checksum or length mismatch");
                }
                CheckCompatibilityCrc(name, Word(entry.Offset + consumed - 8), ~crc, diagnostic);
                return crc;
            }
            catch (Exception error) when (error is InvalidDataException || error is IOException)
            {
                output.Reset();
                uint? compatible;
                try
                {
                    compatible = ReadReferenceGzip(name, entry.Offset, entry.Size, dataOffset, limit, output, diagnostic);
                }
                catch (Exception referenceError) when (referenceError is InvalidDataException || referenceError is IOException)
                {
                    throw new RuntimeFault($"package: invalid gzip {name}: {referenceError.Message}");
                }
                if (compatible is uint crc)
                {
                    consumed = entry.Size;
                    return crc;
                }
                throw new RuntimeFault($"package: invalid gzip {name}: {error.Message}");
            }
        }

        private uint? ReadReferenceGzip(string name, int offset, int size, int dataOffset, int limit,
            InflateBuffer output, Action<string>? diagnostic)
        {
            if (size < dataOffset + 8)
            {
                return null;
            }
            uint declared = Word(offset + size - 4);
            if (declared == 0 || declared > limit)
            {
                throw new RuntimeFault($"package: {name} exceeds decompression limit");
            }
            // The reference inflater has a zero-initialized 32KiB dictionary, including
            // back-references preceding the first output byte. Seed only that bounded history.
            int packedLength = size - dataOffset - 8;
            using var input = new ZipInput(bytes, offset + dataOffset, packedLength, seedHistory: true);
            using var decoder = new DeflateStream(input, CompressionMode.Decompress);
            const int blockSize = 4096;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(blockSize);
            int discarded = 0;
            try
            {
                int count;
                while ((count = decoder.Read(buffer, 0, blockSize)) > 0)
                {
                    int prefix = Math.Min(count, ReferenceHistoryBytes - discarded);
                    for (int index = 0; index < prefix; index++)
                    {
                        if (buffer[index] != 0)
                        {
                            throw new RuntimeFault("package: initial DEFLATE history was modified");
                        }
                    }
                    discarded += prefix;
                    if (!output.Append(buffer.AsSpan(prefix, count - prefix), declared, declared))
                    {
                        throw new RuntimeFault($"package: invalid gzip {name}: expanded data exceeds declared length");
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            // Older resource writers count their four-byte stale tail in ISIZE. Do not
            // accept arbitrary padding or an incomplete member that exhausted its input.
            int padding = (int)declared - output.Length;
            if (discarded != ReferenceHistoryBytes || output.Length == 0 || input.Position != input.Length || padding > 4)
            {
                throw new RuntimeFault($"package: invalid gzip {name}: initial-history length mismatch");
            }
            if (padding != 0)
            {
                Span<byte> tail = stackalloc byte[padding];
                tail.Clear();
                output.Append(tail, declared, declared);
            }
            string message = $"compatibility initial DEFLATE history: {name}; bounded 32768 zero bytes, " +
                $"declared={declared}, original package bytes unchanged";
            if (!compatibilityDiagnostics.Contains(message))
            {
                compatibilityDiagnostics.Add(message);
            }
            diagnostic?.Invoke(message);
            uint crc = Crc32.Update(uint.MaxValue, output.Bytes);
            CheckCompatibilityCrc(name, Word(offset + size - 8), ~crc, diagnostic);
            return crc;
        }

        private uint ReadZip(string name, int offset, int size, int dataOffset, int limit, InflateBuffer output,
            out int consumed, Action<string>? diagnostic)
        {
            int end = offset + size;
            void Require(int position, int length)
            {
                if (position < offset || length < 0 || (long)position + length > end)
                {
                    throw new RuntimeFault($"package: invalid ZIP {name}: truncated record");
                }
            }
            int flags = bytes[offset + 6] | bytes[offset + 7] << 8;
            int method = bytes[offset + 8] | bytes[offset + 9] << 8;
            if ((flags & 1) != 0 || method != 8)
            {
                throw new RuntimeFault($"package: invalid ZIP {name}: unsupported encryption or method");
            }
            int data = offset + dataOffset;
            using var input = new ZipInput(bytes, data, end - data);
            try
            {
                uint sizeHint = (flags & 8) == 0 ? Word(offset + 22) : 0;
                if (!Inflate(input, output, limit, sizeHint))
                {
                    throw new RuntimeFault($"package: {name} exceeds decompression limit");
                }
            }
            catch (InvalidDataException error)
            {
                throw new RuntimeFault($"package: invalid ZIP {name}: {error.Message}");
            }
            int compressedEnd = data + checked((int)input.Position);
            int trailer = compressedEnd;
            int checksum = offset + 14;
            if ((flags & 8) != 0)
            {
                Require(trailer, 4);
                checksum = Word(trailer) == 0x08074b50 ? trailer + 4 : trailer;
                Require(checksum, 12);
                trailer = checksum + 12;
            }
            if (Word(checksum + 4) != input.Position || Word(checksum + 8) != output.Length)
            {
                throw new RuntimeFault($"package: invalid ZIP {name}: checksum or length mismatch");
            }
            if (trailer + 4 <= end && Word(trailer) == 0x04034b50)
            {
                throw new RuntimeFault($"package: invalid ZIP {name}: multiple local records");
            }
            uint crc = Crc32.Update(uint.MaxValue, output.Bytes);
            CheckCompatibilityCrc(name, Word(checksum), ~crc, diagnostic);
            // The reference unzip publishes eight bytes past the deflate stream.
            // A local-only record has no such bytes, so keep its cursor in bounds.
            consumed = (end - compressedEnd >= 8 ? compressedEnd + 8 : compressedEnd) - offset;
            return crc;
        }

        internal static bool Inflate(Stream input, InflateBuffer output, long limit, uint sizeHint = 0)
        {
            const int blockSize = 4096;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(blockSize);
            try
            {
                using var decoder = new DeflateStream(input, CompressionMode.Decompress, true);
                int count;
                while ((count = decoder.Read(buffer, 0, blockSize)) != 0)
                {
                    if (!output.Append(buffer.AsSpan(0, count), limit, sizeHint)) return false;
                }
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        internal sealed class InflateBuffer : IDisposable
        {
            private const int MaximumPooledBytes = 256 * 1024;
            private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Create(MaximumPooledBytes, 1);
            private static readonly WeakByteBuffer LargeSpare = new WeakByteBuffer();
            private readonly bool transferOwnership;
            private byte[] buffer = Array.Empty<byte>();
            private bool rented;

            internal InflateBuffer(bool transferOwnership = false) => this.transferOwnership = transferOwnership;

            internal int Length { get; private set; }
            internal ReadOnlySpan<byte> Bytes => buffer.AsSpan(0, Length);

            internal bool Append(ReadOnlySpan<byte> block, long limit, uint sizeHint)
            {
                long next = (long)Length + block.Length;
                if (next > limit) return false;
                if (next > buffer.Length)
                {
                    // A header may cap growth, but only decoded bytes can trigger it.
                    long capacity = Math.Min(limit, Math.Max(next, 2L * buffer.Length));
                    if (sizeHint >= next) capacity = Math.Min(capacity, sizeHint);
                    // Package.Read can hand an exact-sized, privately owned buffer to its caller.
                    bool reuseNext = !transferOwnership || (capacity <= MaximumPooledBytes && capacity != sizeHint);
                    byte[] grown = reuseNext ? RentBuffer((int)capacity) : new byte[(int)capacity];
                    Bytes.CopyTo(grown);
                    ReturnBuffer();
                    buffer = grown;
                    rented = reuseNext;
                }
                block.CopyTo(buffer.AsSpan(Length));
                Length = (int)next;
                return true;
            }

            internal byte[] TakeBytes() => !rented && buffer.Length == Length ? buffer : Bytes.ToArray();

            private static byte[] RentBuffer(int capacity) =>
                capacity <= MaximumPooledBytes ? Pool.Rent(capacity) : LargeSpare.Rent(capacity);

            private void ReturnBuffer()
            {
                if (!rented) return;
                if (buffer.Length <= MaximumPooledBytes)
                {
                    Pool.Return(buffer);
                }
                else
                {
                    LargeSpare.Return(buffer);
                }
            }

            internal void Reset()
            {
                ReturnBuffer();
                buffer = Array.Empty<byte>();
                Length = 0;
                rented = false;
            }

            public void Dispose() => Reset();
        }

        private void CheckCompatibilityCrc(string name, uint stored, uint actual, Action<string>? diagnostic)
        {
            if (stored == actual)
            {
                return;
            }
            string message = $"compatibility CRC mismatch: {name}, stored={stored:x8}, actual={actual:x8}; " +
                "decoded length and bounds verified, reference continues";
            if (!compatibilityDiagnostics.Contains(message))
            {
                compatibilityDiagnostics.Add(message);
            }
            diagnostic?.Invoke(message);
        }

        // Exact consumption is needed when a ZIP data descriptor follows the DEFLATE bytes.
        private sealed class ZipInput : MemoryStream
        {
            // Non-final stored block with a 32 KiB payload of zeros.
            private static readonly byte[] HistoryHeader = { 0, 0, 0x80, 0xff, 0x7f };
            private const int SeedLength = 5 + ReferenceHistoryBytes;
            private int seedOffset;

            internal ZipInput(byte[] bytes, int offset, int length, bool seedHistory = false)
                : base(bytes, offset, length, false)
            {
                seedOffset = seedHistory ? 0 : SeedLength;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (count != 0 && seedOffset < SeedLength)
                {
                    int read = Math.Min(count, SeedLength - seedOffset);
                    Span<byte> seed = buffer.AsSpan(offset, read);
                    seed.Clear();
                    if (seedOffset < HistoryHeader.Length)
                    {
                        int headerBytes = Math.Min(read, HistoryHeader.Length - seedOffset);
                        HistoryHeader.AsSpan(seedOffset, headerBytes).CopyTo(seed);
                    }
                    seedOffset += read;
                    return read;
                }
                if (count != 0 && Position == Length)
                {
                    throw new InvalidDataException("truncated compressed data");
                }
                return base.Read(buffer, offset, Math.Min(count, 1));
            }
        }

        private uint Word(int offset) => Word(bytes, offset);

        private static uint Word(ReadOnlySpan<byte> image, int offset)
        {
            Range(offset, 4, image.Length);
            return BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(offset));
        }

        private string EntryName(int offset, int length) =>
            ScriptChunk.Bytes.GetString(ReadEntryName(bytes.AsSpan(offset, length)));

        private static ReadOnlySpan<byte> ReadEntryName(ReadOnlySpan<byte> name)
        {
            int terminator = name.IndexOf((byte)0);
            return terminator < 0 ? name : name.Slice(0, terminator);
        }

        private static void Range(int offset, int count, int end)
        {
            if (offset < 0 || count < 0 || (long)offset + count > end)
            {
                throw new RuntimeFault($"package: truncated index at {offset}");
            }
        }
    }
}

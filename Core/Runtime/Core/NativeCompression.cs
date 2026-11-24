using System;
using System.IO;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private uint AllocateResource(ReadOnlySpan<byte> contents)
        {
            uint allocator = Memory.U32(table);
            if (Trace.Count < TraceLimit)
            {
                Trace.Add($"resource allocator={allocator:x8}, size={contents.Length}, " +
                    $"heapBase={Memory.U32(Memory.U32(table + 108 * 4)):x8}, " +
                    $"heapLength={Memory.U32(Memory.U32(table + 109 * 4)):x8}, " +
                    $"heapEnd={Memory.U32(Memory.U32(table + 110 * 4)):x8}, " +
                    $"heapLeft={Memory.U32(Memory.U32(table + 111 * 4)):x8}");
            }
            uint address = Cpu.Invoke(allocator, stackalloc uint[] { (uint)contents.Length });
            if (address != 0)
            {
                try
                {
                    Memory.Write(address, contents);
                }
                catch
                {
                    Cpu.Invoke(Memory.U32(table + 4), address, (uint)contents.Length);
                    throw;
                }
            }
            return address;
        }

        private uint ReadPackageResource(Package selected, string resource, (int Offset, int Size) stored,
            uint resident, uint lengthOutput, out bool owned)
        {
            owned = false;
            Memory.Check(lengthOutput, 4);
            selected.ValidateStoredRange(resource, stored);
            Memory.Put32(lengthOutput, (uint)stored.Size);
            uint source = resident != 0 ? resident + (uint)stored.Offset :
                AllocateResource(selected.Image.Slice(stored.Offset, stored.Size));
            if (source == 0)
            {
                return 0;
            }
            bool releaseSource = resident == 0;
            uint destination = 0;
            uint decodedLength = 0;
            try
            {
                try
                {
                    Memory.Put32(inflateFields, source);
                    Memory.Put32(inflateFields + 8, 2);
                    Memory.Put32(inflateFields + 12, 0);
                    int header = selected.CompressionOffset(resource, stored);
                    if (header == 0)
                    {
                        owned = releaseSource;
                        releaseSource = false;
                        return source;
                    }
                    Memory.Put32(inflateFields + 8, (uint)header);
                    using var decoded = new Package.InflateBuffer();
                    uint checksum = selected.ReadCompressedEntry(resource, stored, header, Package.MaxBytes, decoded, out int consumed, Record);
                    decodedLength = (uint)decoded.Length;
                    Memory.Put32(lengthOutput, decodedLength);
                    // Capture this before writing: an observer can modify the bytes and unsubscribe.
                    bool reuseChecksum = !Memory.HasWriteObservers && ReferenceEquals(Cpu.HostCall, defaultHostCall) &&
                        Memory.U32(table) == 0xf0000000u;
                    destination = AllocateResource(decoded.Bytes);
                    Memory.Put32(inflateFields + 4, destination);
                    if (destination == 0)
                    {
                        return 0;
                    }
                    if (reuseChecksum)
                    {
                        nativeCrc = checksum;
                    }
                    else
                    {
                        UpdateCrc(0, 0);
                        UpdateCrc(destination, decodedLength);
                    }
                    Memory.Put32(inflateFields + 8, (uint)consumed);
                    Memory.Put32(inflateFields + 12, decodedLength);
                }
                finally
                {
                    if (releaseSource)
                    {
                        Cpu.Invoke(Memory.U32(table + 4), source, (uint)stored.Size);
                    }
                }
            }
            catch
            {
                if (destination != 0)
                {
                    Cpu.Invoke(Memory.U32(table + 4), destination, decodedLength);
                }
                throw;
            }
            owned = true;
            return destination;
        }

        private uint UnzipNative()
        {
            uint source = Memory.U32(inflateFields);
            uint destination = Memory.U32(inflateFields + 4);
            uint cursor = Memory.U32(inflateFields + 8);
            uint capacity = Memory.MappedEnd;
            Memory.Check(source, 4);
            Memory.Check(destination, 0);
            uint sourceLimit = capacity;
            if (Memory.TryGetAllocationRemaining(source, out uint sourceRemaining))
            {
                sourceLimit = Math.Min(sourceLimit, checked(source + sourceRemaining));
            }
            if ((ulong)source + cursor >= sourceLimit)
            {
                throw new RuntimeFault("unzip input cursor outside guest memory");
            }
            uint outputLimit = capacity - destination;
            if (Memory.TryGetAllocationRemaining(destination, out uint destinationRemaining))
            {
                outputLimit = Math.Min(outputLimit, destinationRemaining);
            }
            void CheckSource(uint address, uint length)
            {
                if (address < source || (ulong)address + length > sourceLimit)
                {
                    throw new RuntimeFault("unzip truncated input at allocation boundary");
                }
            }
            CheckSource(source, 4);
            using var input = new InflateInput(Memory, source + cursor, sourceLimit);
            using var output = new Package.InflateBuffer();
            try
            {
                if (!Package.Inflate(input, output, outputLimit))
                {
                    throw new RuntimeFault("unzip output exceeds guest memory or destination allocation");
                }
            }
            catch (InvalidDataException error)
            {
                Record("unzip invalid compressed data: " + error.Message);
                return uint.MaxValue;
            }
            uint compressedEnd = checked(source + cursor + (uint)input.Position);
            uint end = compressedEnd;
            uint expectedCrc;
            uint expectedLength;
            bool isZip = Memory.U32(source) == 0x04034b50;
            if (isZip)
            {
                CheckSource(source, 30);
                uint flags = Memory.U16(source + 6);
                if ((flags & 1) != 0 || Memory.U16(source + 8) != 8)
                {
                    throw new RuntimeFault("unzip unsupported ZIP encryption or method");
                }
                if ((flags & 8) != 0)
                {
                    CheckSource(end, 4);
                    if (Memory.U32(end) == 0x08074b50)
                    {
                        end += 4;
                    }
                    CheckSource(end, 12);
                    expectedCrc = Memory.U32(end);
                    expectedLength = Memory.U32(end + 8);
                    if (Memory.U32(end + 4) != input.Position)
                    {
                        throw new RuntimeFault("unzip ZIP compressed length mismatch");
                    }
                    end += 12;
                }
                else
                {
                    expectedCrc = Memory.U32(source + 14);
                    expectedLength = Memory.U32(source + 22);
                    if (Memory.U32(source + 18) != input.Position)
                    {
                        throw new RuntimeFault("unzip ZIP compressed length mismatch");
                    }
                }
            }
            else
            {
                CheckSource(end, 8);
                expectedCrc = Memory.U32(end);
                expectedLength = Memory.U32(end + 4);
                end += 8;
            }
            ReadOnlySpan<byte> decoded = output.Bytes;
            uint checksum = Crc32.Update(uint.MaxValue, decoded);
            if (expectedLength != decoded.Length)
            {
                throw new RuntimeFault("unzip checksum or length mismatch");
            }
            if (expectedCrc != ~checksum)
            {
                Record($"compatibility CRC mismatch: native unzip, stored={expectedCrc:x8}, " +
                    $"actual={~checksum:x8}; decoded length and bounds verified, reference continues");
            }
            Memory.Write(destination, decoded);
            nativeCrc = checksum;
            // The reference unzip reads eight bytes after ZIP deflate even when the
            // local header already carries the checksum. Keep a short local record bounded.
            uint publishedEnd = isZip && (ulong)compressedEnd + 8 <= sourceLimit
                ? compressedEnd + 8 : end;
            Memory.Put32(inflateFields + 8, publishedEnd - source);
            Memory.Put32(inflateFields + 12, (uint)decoded.Length);
            Record($"unzip completed: consumed={publishedEnd - source}, output={decoded.Length}");
            return 0;
        }

        // One byte per read prevents DeflateStream from consuming the trailer or next record.
        private sealed class InflateInput : Stream
        {
            private readonly GuestMemory memory;
            private readonly uint start;
            private readonly uint end;
            private uint cursor;

            internal InflateInput(GuestMemory memory, uint start, uint end)
            {
                this.memory = memory;
                this.start = start;
                this.end = end;
                cursor = start;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => end - start;
            public override long Position
            {
                get => cursor - start;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (count == 0)
                {
                    return 0;
                }
                if (cursor == end)
                {
                    throw new RuntimeFault("unzip truncated input at guest memory boundary");
                }
                buffer[offset] = (byte)memory.U8(cursor++);
                return 1;
            }

            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}

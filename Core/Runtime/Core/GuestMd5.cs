using System;

namespace CSharpMrp
{
    // RFC 1321 algorithm; guest ABI: bit count[2], state[4], partial block[64].
    // State stays in guest RAM, including across native copies of the 88-byte context.
    internal static class GuestMd5
    {
        private static readonly int[] Rotations = { 7, 12, 17, 22, 5, 9, 14, 20, 4, 11, 16, 23, 6, 10, 15, 21 };
        private static readonly uint[] Constants = MakeConstants();

        private static uint[] MakeConstants()
        {
            var values = new uint[64];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = (uint)(Math.Abs(Math.Sin(i + 1)) * 4294967296.0);
            }
            return values;
        }

        internal static void Init(GuestMemory memory, uint context)
        {
            memory.Check(context, 88);
            memory.Put32(context, 0);
            memory.Put32(context + 4, 0);
            memory.Put32(context + 8, 0x67452301);
            memory.Put32(context + 12, 0xefcdab89);
            memory.Put32(context + 16, 0x98badcfe);
            memory.Put32(context + 20, 0x10325476);
        }

        internal static void Append(GuestMemory memory, uint context, uint input, int length)
        {
            memory.Check(context, 88);
            if (length <= 0)
            {
                return;
            }
            ReadOnlySpan<byte> data = memory.ReadSpan(input, (uint)length);
            // Context writes and observers may change guest bytes during the update.
            if (memory.HasWriteObservers || ((ulong)input + (uint)length > context && input < context + 88))
            {
                data = data.ToArray();
            }
            Append(memory, context, data);
        }

        private static void Append(GuestMemory memory, uint context, ReadOnlySpan<byte> input)
        {
            ulong bits = memory.U32(context) | (ulong)memory.U32(context + 4) << 32;
            int used = (int)((bits >> 3) & 63);
            ulong total = unchecked(bits + (ulong)input.Length * 8);
            memory.Put32(context, (uint)total);
            memory.Put32(context + 4, (uint)(total >> 32));
            Span<byte> block = stackalloc byte[64];
            memory.ReadSpan(context + 24, 64).CopyTo(block);
            Span<uint> state = stackalloc uint[4];
            bool batch = !memory.HasWriteObservers && input.Length >= 64 - used;
            if (batch) ReadState(memory, context, state);
            while (input.Length > 0)
            {
                int count = Math.Min(64 - used, input.Length);
                input.Slice(0, count).CopyTo(block.Slice(used));
                input = input.Slice(count);
                used += count;
                if (used == 64)
                {
                    if (!batch) ReadState(memory, context, state);
                    Process(state, block);
                    if (!batch) WriteState(memory, context, state);
                    used = 0;
                }
            }
            if (batch) WriteState(memory, context, state);
            memory.Write(context + 24, block);
        }

        internal static void Finish(GuestMemory memory, uint context, uint output)
        {
            memory.Check(context, 88);
            memory.Check(output, 16);
            Span<byte> length = stackalloc byte[8];
            memory.ReadSpan(context, 8).CopyTo(length);
            int used = (int)((memory.U32(context) >> 3) & 63);
            Span<byte> padding = stackalloc byte[used < 56 ? 56 - used : 120 - used];
            padding.Clear();
            padding[0] = 128;
            Append(memory, context, padding);
            Append(memory, context, length);
            memory.Write(output, memory.ReadSpan(context + 8, 16));
        }

        private static void ReadState(GuestMemory memory, uint context, Span<uint> state)
        {
            for (int i = 0; i < state.Length; i++)
            {
                state[i] = memory.U32(context + 8 + (uint)i * 4);
            }
        }

        private static void WriteState(GuestMemory memory, uint context, ReadOnlySpan<uint> state)
        {
            for (int i = 0; i < state.Length; i++)
            {
                memory.Put32(context + 8 + (uint)i * 4, state[i]);
            }
        }

        internal static byte[] Digest(ReadOnlySpan<byte> input)
        {
            Span<uint> state = stackalloc uint[] { 0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476 };
            ulong bits = (ulong)input.Length * 8;
            while (input.Length >= 64)
            {
                Process(state, input.Slice(0, 64));
                input = input.Slice(64);
            }
            Span<byte> tail = stackalloc byte[128];
            tail.Clear();
            input.CopyTo(tail);
            tail[input.Length] = 128;
            int end = input.Length < 56 ? 64 : 128;
            for (int i = 0; i < 8; i++)
            {
                tail[end - 8 + i] = (byte)(bits >> (i * 8));
            }
            Process(state, tail.Slice(0, 64));
            if (end == 128)
            {
                Process(state, tail.Slice(64, 64));
            }
            var digest = new byte[16];
            for (int i = 0; i < digest.Length; i++)
            {
                digest[i] = (byte)(state[i / 4] >> ((i % 4) * 8));
            }
            return digest;
        }

        private static void Process(Span<uint> state, ReadOnlySpan<byte> block)
        {
            Span<uint> words = stackalloc uint[16];
            for (int i = 0; i < words.Length; i++)
            {
                int p = i * 4;
                words[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(p, 4));
            }
            uint a = state[0];
            uint b = state[1];
            uint c = state[2];
            uint d = state[3];
            for (int i = 0; i < 64; i++)
            {
                uint f = i < 16 ? (b & c) | (~b & d) : i < 32 ? (d & b) | (~d & c) :
                    i < 48 ? b ^ c ^ d : c ^ (b | ~d);
                int word = i < 16 ? i : i < 32 ? (5 * i + 1) & 15 : i < 48 ? (3 * i + 5) & 15 : (7 * i) & 15;
                int shift = Rotations[(i / 16) * 4 + i % 4];
                uint sum = unchecked(a + f + words[word] + Constants[i]);
                uint next = unchecked(b + ((sum << shift) | (sum >> (32 - shift))));
                a = d;
                d = c;
                c = b;
                b = next;
            }
            state[0] = unchecked(state[0] + a);
            state[1] = unchecked(state[1] + b);
            state[2] = unchecked(state[2] + c);
            state[3] = unchecked(state[3] + d);
        }
    }
}

using System;
using System.Runtime.CompilerServices;

namespace CSharpMrp
{
    // A unique script string whose bytes are also addressable by guest C code.
    // The session owns its allocation. Identity remains stable when bytes change.
    internal sealed class ScriptMutableString
    {
        private readonly GuestMemory memory;

        internal GuestMemory Memory => memory;

        internal uint Pointer { get; }
        internal int Length { get; }
        internal string? InternedText { get; }
        internal uint AllocationHash { get; }

        internal ScriptMutableString(GuestMemory memory, uint pointer, string internedText)
        {
            this.memory = memory;
            Pointer = pointer;
            Length = internedText.Length;
            InternedText = internedText;
            AllocationHash = ScriptTableAllocation.HashText(internedText);
        }

        internal ScriptMutableString(GuestMemory memory, uint pointer, int length)
        {
            this.memory = memory;
            Pointer = pointer;
            Length = length;
            AllocationHash = HashBytes(length, memory.ReadSpan(pointer, (uint)length));
        }

        // An empty byte span requests a zero-filled string of the given length.
        internal ScriptMutableString(GuestMemory memory, int length, ReadOnlySpan<byte> bytes)
        {
            this.memory = memory;
            Length = length;
            AllocationHash = HashBytes(length, bytes);
            Pointer = memory.AllocateVmString((uint)length, bytes);
        }

        private static uint HashBytes(int length, ReadOnlySpan<byte> bytes)
        {
            uint hash = (uint)length;
            int step = (length >> 5) + 1;
            for (int index = length; index >= step; index -= step)
            {
                hash ^= (hash << 5) + (hash >> 2) + (bytes.IsEmpty ? 0u : bytes[index - 1]);
            }
            return hash;
        }

        internal ReadOnlySpan<byte> Bytes => memory.ReadSpan(Pointer, (uint)Length);
        internal string Text => ScriptChunk.Bytes.GetString(Bytes);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TextEquals(string text) => ScriptChunk.BytesEqualText(Bytes, text.AsSpan());

        public override string ToString() => Text;

        public override int GetHashCode()
        {
            return InternedText?.GetHashCode() ?? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
        }
    }
}

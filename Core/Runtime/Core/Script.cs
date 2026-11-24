using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace CSharpMrp
{
    internal readonly struct ScriptBoolean
    {
        private static readonly object BoxedFalse = false;
        private static readonly object BoxedTrue = true;
        private readonly int value;

        private ScriptBoolean(int value)
        {
            this.value = value;
        }

        internal static object Box(bool value) => value ? BoxedTrue : BoxedFalse;

        internal static object FromField(int value) => value switch
        {
            0 => BoxedFalse,
            1 => BoxedTrue,
            _ => new ScriptBoolean(value)
        };

        internal uint AllocationHash => unchecked((uint)value);

        public override bool Equals(object? other)
        {
            return other is ScriptBoolean boolean && value == boolean.value;
        }

        public override int GetHashCode()
        {
            return value;
        }

        public override string ToString()
        {
            return "true";
        }
    }

    public readonly struct ScriptLocal
    {
        public readonly string? Name;
        public readonly int Start, End;

        public ScriptLocal(string? name, int start, int end)
        {
            Name = name;
            Start = start;
            End = end;
        }
    }

    public sealed class ScriptPrototype
    {
        public string Source = "?";
        public int LineDefined;
        public int[] Lines = Array.Empty<int>();
        public ScriptLocal[] Locals = Array.Empty<ScriptLocal>();
        public string?[] UpvalueNames = Array.Empty<string?>();
        public int Parameters, Upvalues, Stack;
        public bool Vararg;
        public object?[] Constants = Array.Empty<object?>();
        public ScriptPrototype[] Children = Array.Empty<ScriptPrototype>();
        public uint[] Code = Array.Empty<uint>();
        internal object?[]? SharedDebugStrings;
        internal ScriptTable? CompilationTable;

        internal string CurrentSource => SharedDebugStrings == null ? Source : ScriptVm.Text(SharedDebugStrings[0]);

        internal string? LocalName(int index)
        {
            object? value = SharedDebugStrings == null ? Locals[index].Name : SharedDebugStrings[index + 1];
            return value == null ? null : ScriptVm.Text(value);
        }

        internal string? UpvalueName(int index)
        {
            object? value = SharedDebugStrings == null ? UpvalueNames[index] : SharedDebugStrings[1 + Locals.Length + index];
            return value == null ? null : ScriptVm.Text(value);
        }
    }

    public static partial class ScriptChunk
    {
        public static readonly Encoding Bytes = Encoding.GetEncoding(28591);
        private static readonly bool UseStringVectors = Vector.IsHardwareAccelerated &&
            (RuntimeInformation.ProcessArchitecture is Architecture.X86 or Architecture.X64);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool BytesEqualText(ReadOnlySpan<byte> bytes, ReadOnlySpan<char> text)
        {
            if (bytes.Length != text.Length) return false;
            int index = 0;
            if (bytes.Length >= 32 && UseStringVectors)
            {
                if (!EqualVectorPrefix(bytes, text, out index)) return false;
            }
            for (; index < bytes.Length; index++)
            {
                if (bytes[index] != text[index]) return false;
            }
            return true;
        }

        private static bool EqualVectorPrefix(ReadOnlySpan<byte> bytes, ReadOnlySpan<char> text, out int index)
        {
            var input = MemoryMarshal.Cast<byte, Vector<byte>>(bytes);
            var expected = MemoryMarshal.Cast<char, Vector<ushort>>(text);
            for (int i = 0; i < input.Length; i++)
            {
                Vector.Widen(input[i], out var low, out var high);
                if (!Vector.EqualsAll(low, expected[i * 2]) || !Vector.EqualsAll(high, expected[i * 2 + 1]))
                {
                    index = i * Vector<byte>.Count;
                    return false;
                }
            }
            index = input.Length * Vector<byte>.Count;
            return true;
        }

        internal static int CompareBytesToText(ReadOnlySpan<byte> bytes, ReadOnlySpan<char> text)
        {
            int length = Math.Min(bytes.Length, text.Length);
            int index = 0;
            if (length >= 32 && UseStringVectors)
            {
                EqualVectorPrefix(bytes.Slice(0, length), text.Slice(0, length), out index);
            }
            else if (length > 16)
            {
                Span<char> decoded = stackalloc char[256];
                while (index < length)
                {
                    int count = Math.Min(decoded.Length, length - index);
                    Span<char> chunk = decoded.Slice(0, count);
                    Bytes.GetChars(bytes.Slice(index, count), chunk);
                    int order = ((ReadOnlySpan<char>)chunk).SequenceCompareTo(text.Slice(index, count));
                    if (order != 0) return order;
                    index += count;
                }
            }
            for (; index < length; index++)
            {
                int order = bytes[index] - text[index];
                if (order != 0) return order;
            }
            return bytes.Length - text.Length;
        }

        public static ScriptPrototype Load(byte[] bytes, string name)
        {
            if (bytes.Length == 0 || bytes[0] != 27)
            {
                return ScriptSource.Compile(bytes, name);
            }
            var reader = new Reader(bytes, name);
            return reader.Load();
        }

        private sealed class Reader
        {
            private readonly byte[] data;
            private readonly string name;
            private int cursor;
            private int elements;
            private bool bigEndian;

            public Reader(byte[] bytes, string name)
            {
                if (bytes.Length > Package.MaxBytes)
                {
                    throw new RuntimeFault($"script {name}: chunk size limit");
                }
                data = bytes;
                this.name = name;
            }

            public ScriptPrototype Load()
            {
                if (Byte() != 27 || Byte() != 'M' || Byte() != 'R' || Byte() != 'P')
                {
                    throw Error("unsupported script signature");
                }
                int version = Byte();
                int byteOrder = Byte();
                if (version < 0x50 || version > 0x80 || byteOrder > 1)
                {
                    throw Error("unsupported version or endianness");
                }
                bigEndian = byteOrder == 0;
                if (version == 0x50)
                {
                    foreach (int size in new[] { 4, 4, 4, 6, 8, 9, 9, 4 })
                    {
                        if (Byte() != size)
                        {
                            throw Error("unsupported 0x50 chunk layout");
                        }
                    }
                    if (Int() != 31415926)
                    {
                        throw Error("unsupported number representation");
                    }
                }
                var result = Function(name, 0);
                if (cursor != data.Length)
                {
                    throw Error("trailing chunk bytes");
                }
                return result;
            }

            private RuntimeFault Error(string reason)
            {
                return new RuntimeFault($"script {name} offset {cursor}: {reason}");
            }

            private byte Byte()
            {
                if (cursor >= data.Length)
                {
                    throw Error("truncated chunk");
                }
                return data[cursor++];
            }

            private int Int()
            {
                if (bigEndian)
                {
                    return Byte() << 24 | Byte() << 16 | Byte() << 8 | Byte();
                }
                return Byte() | Byte() << 8 | Byte() << 16 | Byte() << 24;
            }

            private int Count()
            {
                int value = Int();
                if (value < 0 || value > 100000 || (long)elements + value > 200000)
                {
                    throw Error("element count limit");
                }
                elements += value;
                return value;
            }

            private int Nonnegative()
            {
                int value = Int();
                if (value < 0)
                {
                    throw Error("negative debug integer");
                }
                return value;
            }

            private string? Text()
            {
                int length = Int();
                if (length == 0)
                {
                    return null;
                }
                if (length < 1 || (long)cursor + length > data.Length || data[cursor + length - 1] != 0)
                {
                    throw Error("invalid string range or terminator");
                }
                string value = Bytes.GetString(data, cursor, length - 1);
                cursor += length;
                return value;
            }

            private ScriptPrototype Function(string parent, int depth)
            {
                if (depth > 64)
                {
                    throw Error("prototype depth limit");
                }
                var result = new ScriptPrototype { Source = Text() ?? parent };
                result.LineDefined = Nonnegative();
                result.Upvalues = Byte();
                result.Parameters = Byte();
                result.Vararg = Byte() != 0;
                result.Stack = Byte();
                if (result.Stack < 1 || result.Stack > 250 || result.Parameters > result.Stack)
                {
                    throw Error("invalid register count");
                }
                int lines = Count();
                result.Lines = new int[lines];
                for (int i = 0; i < lines; i++)
                {
                    result.Lines[i] = Int();
                }
                int locals = Count();
                result.Locals = new ScriptLocal[locals];
                for (int i = 0; i < locals; i++)
                {
                    result.Locals[i] = new ScriptLocal(Text(), Nonnegative(), Nonnegative());
                }
                int upvalueNames = Count();
                if (upvalueNames != 0 && upvalueNames != result.Upvalues)
                {
                    throw Error("upvalue names mismatch");
                }
                result.UpvalueNames = new string?[upvalueNames];
                for (int i = 0; i < upvalueNames; i++)
                {
                    result.UpvalueNames[i] = Text();
                }
                result.Constants = new object?[Count()];
                for (int i = 0; i < result.Constants.Length; i++)
                {
                    result.Constants[i] = Byte() switch
                    {
                        0 => null,
                        3 => Int(),
                        4 => Text() ?? "",
                        _ => throw Error("unsupported constant type")
                    };
                }
                result.Children = new ScriptPrototype[Count()];
                for (int i = 0; i < result.Children.Length; i++)
                {
                    result.Children[i] = Function(result.Source, depth + 1);
                }
                result.Code = new uint[Count()];
                for (int i = 0; i < result.Code.Length; i++)
                {
                    result.Code[i] = (uint)Int();
                }
                if (lines != 0 && lines != result.Code.Length)
                {
                    throw Error("line table length mismatch");
                }
                ValidateCode(result);
                return result;
            }

            private void ValidateCode(ScriptPrototype function)
            {
                if (function.Code.Length == 0 || (function.Code[function.Code.Length - 1] & 63) != 27)
                {
                    throw Error("code must end with RETURN");
                }
                if (function.Parameters + (function.Vararg ? 1 : 0) > function.Stack)
                {
                    throw Error("invalid parameter register count");
                }
                for (int pc = 0; pc < function.Code.Length; pc++)
                {
                    uint instruction = function.Code[pc];
                    int op = (int)(instruction & 63);
                    int a = (int)(instruction >> 24);
                    int b = (int)(instruction >> 15 & 511);
                    int c = (int)(instruction >> 6 & 511);
                    int bx = (int)(instruction >> 6 & 262143);
                    void Check(bool condition, string reason)
                    {
                        if (!condition)
                        {
                            throw Error($"invalid code at instruction {pc}: {reason}");
                        }
                    }
                    void Register(int index)
                    {
                        Check(index < function.Stack, "register outside prototype");
                    }
                    void Operand(int index)
                    {
                        Check(index < function.Stack || index >= 250 && index - 250 < function.Constants.Length,
                            "register/constant operand outside prototype");
                    }
                    Check(op <= 38, "unknown opcode");
                    Register(a);
                    if (op is 0 or 3 or 6 or 11 or 17 or 18 or 19 or 24 or 35)
                    {
                        Register(b);
                    }
                    if (op is 9 or 12 or 13 or 14 or 15 or 16 or 21 or 22 or 23 or 36 or 37 or 38)
                    {
                        Operand(b);
                    }
                    if (op is 6 or 9 or 11 or 12 or 13 or 14 or 15 or 16 or 19 or 21 or 22 or 23 or 36 or 37 or 38)
                    {
                        Operand(c);
                    }
                    if (op is 1 or 5 or 7)
                    {
                        Check(bx < function.Constants.Length, "constant outside prototype");
                        Check(op == 1 || ScriptVm.IsString(function.Constants[bx]), "global name is not a string");
                    }
                    if (op is 21 or 22 or 23 or 24 or 29)
                    {
                        Check(pc + 2 < function.Code.Length && (function.Code[pc + 1] & 63) == 20,
                            "test must be followed by JMP and a continuation");
                    }
                    switch (op)
                    {
                        case 2:
                            Check(c == 0 || pc + 2 < function.Code.Length, "LOADBOOL skip outside code");
                            break;
                        case 4:
                        case 8:
                            Check(b < function.Upvalues, "upvalue outside prototype");
                            break;
                        case 11:
                            Register(a + 1);
                            break;
                        case 19:
                            Check(c < 250 && b < c, "CONCAT requires at least two registers");
                            break;
                        case 20:
                        case 28:
                        case 30:
                            long target = pc + 1L + bx - 131071;
                            Check(target >= 0 && target < function.Code.Length, "jump outside code");
                            if (op == 28)
                            {
                                Register(a + 2);
                            }
                            break;
                        case 25:
                        case 26:
                            if (b != 0)
                            {
                                Register(a + b - 1);
                            }
                            if (c == 0)
                            {
                                Check(pc + 1 < function.Code.Length, "open call lacks continuation");
                                uint next = function.Code[pc + 1];
                                int nextOp = (int)(next & 63);
                                Check(nextOp == 32 || (nextOp is 25 or 26 or 27) && (next >> 15 & 511) == 0,
                                    "open call requires open result consumer");
                            }
                            else if (c > 1)
                            {
                                Register(a + c - 2);
                            }
                            break;
                        case 27:
                            if (b > 1)
                            {
                                Register(a + b - 2);
                            }
                            break;
                        case 29:
                            Register(a + c + 5);
                            Check(pc + 1L + b < function.Code.Length, "iterator continuation outside code");
                            break;
                        case 31:
                            Register(a + (bx & 31) + 1);
                            break;
                        case 34:
                            Check(bx < function.Children.Length, "child outside prototype");
                            int captures = function.Children[bx].Upvalues;
                            Check(pc + captures < function.Code.Length, "truncated closure captures");
                            for (int capture = 1; capture <= captures; capture++)
                            {
                                uint captureOp = function.Code[pc + capture] & 63;
                                Check(captureOp == 0 || captureOp == 4, "invalid closure capture opcode");
                            }
                            break;
                    }
                }
            }
        }
    }

    public sealed class ScriptTable
    {
        private sealed class KeyComparer : IEqualityComparer<object>
        {
            internal bool MatchByIdentity;

            public new bool Equals(object? left, object? right)
            {
                return MatchByIdentity ? ReferenceEquals(left, right) : ScriptVm.RawEqual(left, right);
            }

            public int GetHashCode(object value)
            {
                return ScriptVm.IsFunction(value) ? RuntimeHelpers.GetHashCode(value) : value.GetHashCode();
            }
        }

        internal ScriptTable? Metatable;
        internal readonly ScriptTableAllocation Allocation;
        internal ScriptVm? AllocationOwner;

        public ScriptTable() : this(0, 0)
        {
        }

        internal ScriptTable(int arraySize, int hashBits)
        {
            Allocation = new ScriptTableAllocation(arraySize, hashBits);
        }
        private Dictionary<object, object?> values = new Dictionary<object, object?>(new KeyComparer());
        private readonly List<object> iterationKeys = new List<object>();
        private Dictionary<object, int>? iterationPositions;
        private int? sequenceSize;
        private int sequencePrefix;
        private bool sequenceEndKnown = true;
        internal Dictionary<object, object?> Entries => values;

        internal void ReserveDecodedEntries(int capacity)
        {
            values.EnsureCapacity(capacity);
            iterationKeys.Capacity = capacity;
        }

        internal void ReplaceString(string text, ScriptMutableString replacement)
        {
            bool Matches(object? value) => value is string candidate && candidate == text;
            bool replaceKey = false;
            object? firstValueKey = null;
            List<object>? moreValueKeys = null;
            foreach (KeyValuePair<object, object?> entry in values)
            {
                replaceKey |= Matches(entry.Key);
                if (!Matches(entry.Value)) continue;
                if (firstValueKey == null) firstValueKey = entry.Key;
                else (moreValueKeys ??= new List<object>()).Add(entry.Key);
            }
            int keyPosition = -1;
            for (int index = 0; index < iterationKeys.Count; index++)
            {
                if (!Matches(iterationKeys[index])) continue;
                keyPosition = index;
                break;
            }
            int slot = Allocation.FindStringReplacementSlot(text, replacement, out bool replacementExists);
            if (replacementExists || ((replaceKey || keyPosition >= 0 || slot >= 0) && HasExactStringKey(replacement)))
            {
                // Keep both positions when the string would become the same key object.
                replaceKey = false;
                keyPosition = -1;
                slot = -1;
            }

            Dictionary<object, int>? positions = null;
            if (keyPosition >= 0)
            {
                positions = new Dictionary<object, int>(iterationKeys.Count, values.Comparer);
                FillIterationPositions(positions, keyPosition, replacement);
            }
            if (replaceKey || firstValueKey != null)
            {
                // Keep distinct entries even when two keys temporarily compare equal.
                var comparer = (KeyComparer)values.Comparer;
                bool previousMode = comparer.MatchByIdentity;
                comparer.MatchByIdentity = true;
                try
                {
                    if (replaceKey)
                    {
                        var entries = new Dictionary<object, object?>(values.Count, comparer);
                        foreach (KeyValuePair<object, object?> entry in values)
                        {
                            entries.Add(Matches(entry.Key) ? replacement : entry.Key,
                                Matches(entry.Value) ? replacement : entry.Value);
                        }
                        values = entries;
                    }
                    else
                    {
                        values[firstValueKey!] = replacement;
                        if (moreValueKeys != null)
                        {
                            foreach (object key in moreValueKeys) values[key] = replacement;
                        }
                    }
                }
                finally { comparer.MatchByIdentity = previousMode; }
            }
            if (positions != null)
            {
                iterationKeys[keyPosition] = replacement;
                iterationPositions = positions;
            }
            if (slot >= 0) Allocation.ReplaceStringKeyAt(slot, replacement);
        }

        private bool HasExactStringKey(ScriptMutableString key)
        {
            var comparer = (KeyComparer)values.Comparer;
            bool previousMode = comparer.MatchByIdentity;
            comparer.MatchByIdentity = true;
            try
            {
                return values.ContainsKey(key) || iterationPositions?.ContainsKey(key) == true;
            }
            finally { comparer.MatchByIdentity = previousMode; }
        }

        internal (bool Keys, bool Values) ReadWeakMode()
        {
            switch (this["__mode"])
            {
                case string mode:
                    return (mode.IndexOf('k') >= 0, mode.IndexOf('v') >= 0);
                case ScriptMutableString mutable:
                    // ScriptChunk.Bytes maps each byte directly to a character.
                    ReadOnlySpan<byte> bytes = mutable.Bytes;
                    return (bytes.IndexOf((byte)'k') >= 0, bytes.IndexOf((byte)'v') >= 0);
                default:
                    return default;
            }
        }

        internal void ClearWeakEntries(HashSet<object> live, bool clearKeys, ref List<object>? removed)
        {
            (bool weakKeys, bool weakValues) = Metatable?.ReadWeakMode() ?? default;
            weakKeys &= clearKeys;
            if (weakKeys || weakValues)
            {
                foreach (KeyValuePair<object, object?> entry in values)
                {
                    if (weakKeys && ScriptVm.WeakCollectable(entry.Key) && !live.Contains(entry.Key) ||
                        weakValues && ScriptVm.WeakCollectable(entry.Value) && !live.Contains(entry.Value!))
                    {
                        (removed ??= new List<object>()).Add(entry.Key);
                    }
                }
            }
            if (removed is { Count: > 0 })
            {
                foreach (object key in removed)
                {
                    Allocation.Set(key, false);
                    RemoveEntry(key);
                }
                removed.Clear();
            }
            // The allocation sweep prunes the final history with the complete string marks.
            if (!clearKeys) PruneIterationKeys(live, null);
        }

        internal void PruneAllocationHistory(HashSet<object> live, HashSet<string> liveStrings)
        {
            Allocation.PruneDeadKeys(live, liveStrings);
            PruneIterationKeys(live, liveStrings);
        }

        private void PruneIterationKeys(HashSet<object> live, HashSet<string>? liveStrings)
        {
            if (iterationKeys.Count == values.Count) return;
            int retained = 0;
            for (int index = 0; index < iterationKeys.Count; index++)
            {
                object key = iterationKeys[index];
                bool remove = liveStrings != null && key is string text ? !liveStrings.Contains(text) :
                    (ScriptVm.WeakCollectable(key) || liveStrings != null && key is ScriptMutableString) && !live.Contains(key);
                if (remove && !values.ContainsKey(key)) continue;
                if (retained != index) iterationKeys[retained] = key;
                retained++;
            }
            CompactIterationPositions(retained);
        }

        private void PruneOverwrittenIterationKeys()
        {
            int retained = 0;
            for (int index = 0; index < iterationKeys.Count; index++)
            {
                object key = iterationKeys[index];
                if (!values.ContainsKey(key) && !Allocation.HasTraversalPosition(key)) continue;
                if (retained != index) iterationKeys[retained] = key;
                retained++;
            }
            CompactIterationPositions(retained);
        }

        private void CompactIterationPositions(int retained)
        {
            if (retained == iterationKeys.Count) return;
            iterationKeys.RemoveRange(retained, iterationKeys.Count - retained);
            if (iterationPositions != null) RebuildIterationPositions();
        }

        private Dictionary<object, int> RebuildIterationPositions()
        {
            var positions = iterationPositions ?? new Dictionary<object, int>(iterationKeys.Count, values.Comparer);
            positions.Clear();
            FillIterationPositions(positions);
            return iterationPositions = positions;
        }

        private void FillIterationPositions(Dictionary<object, int> positions, int replacedIndex = -1, object? replacement = null)
        {
            var comparer = (KeyComparer)positions.Comparer;
            bool previousMode = comparer.MatchByIdentity;
            comparer.MatchByIdentity = true;
            try
            {
                for (int index = 0; index < iterationKeys.Count; index++)
                {
                    positions.Add(index == replacedIndex ? replacement! : iterationKeys[index], index);
                }
            }
            finally { comparer.MatchByIdentity = previousMode; }
        }

        internal int SequenceLength()
        {
            if (ScriptVm.TryNumber(this["n"], out int explicitSize) && explicitSize >= 0)
            {
                return explicitSize;
            }
            if (sequenceSize.HasValue && sequenceSize.Value >= 0)
            {
                return sequenceSize.Value;
            }
            if (!sequenceEndKnown)
            {
                while (values.ContainsKey(sequencePrefix + 1)) sequencePrefix++;
                sequenceEndKnown = true;
            }
            return sequencePrefix;
        }

        private void RemoveEntry(object key)
        {
            if (!values.Remove(key)) return;
            if (key is int index)
            {
                if (index > 0 && index <= sequencePrefix)
                {
                    sequencePrefix = index - 1;
                    sequenceEndKnown = true;
                }
            }
            else if (key is not string)
            {
                sequencePrefix = 0;
                sequenceEndKnown = false;
            }
        }

        internal void SetSequenceLength(int length)
        {
            if (ScriptVm.TryNumber(this["n"], out int explicitSize) && explicitSize >= 0)
            {
                this["n"] = length;
            }
            else
            {
                sequenceSize = length;
            }
        }

        internal bool TryNext(object? previous, out object? key, out object? value)
        {
            int index = -1;
            if (previous != null && !(iterationPositions ?? RebuildIterationPositions()).TryGetValue(previous, out index))
            {
                throw new RuntimeFault("invalid table iteration key");
            }
            for (index++; index < iterationKeys.Count; index++)
            {
                object candidate = iterationKeys[index];
                if (values.TryGetValue(candidate, out value))
                {
                    key = candidate;
                    return true;
                }
            }
            key = null;
            value = null;
            return false;
        }

        public object? this[object? key]
        {
            get
            {
                return key != null && values.TryGetValue(key, out var value) ? value : null;
            }
            set
            {
                if (key == null)
                {
                    throw new RuntimeFault("script table: nil key");
                }
                Allocation.Set(key, value != null);
                AllocationOwner?.AccountTableWrite(this, key, value);
                if (value == null)
                {
                    RemoveEntry(key);
                }
                else
                {
                    if (values.Count >= 100000 && !values.ContainsKey(key))
                    {
                        throw new RuntimeFault("script table size limit");
                    }
                    if (iterationPositions == null && iterationKeys.Count == values.Count &&
                        key is not ScriptMutableString { InternedText: not null })
                    {
                        if (values.TryAdd(key, value)) iterationKeys.Add(key);
                        else values[key] = value;
                    }
                    else
                    {
                        var positions = iterationPositions ?? RebuildIterationPositions();
                        if (!positions.ContainsKey(key))
                        {
                            // A deleted key can still resume traversal while its guest slot exists.
                            // Amortize removal of obsolete positions over subsequent insertions.
                            if (iterationKeys.Count - values.Count > Math.Max(64, Allocation.TraversalCapacity))
                            {
                                PruneOverwrittenIterationKeys();
                            }
                            positions.Add(key, iterationKeys.Count);
                            iterationKeys.Add(key);
                        }
                        values[key] = value;
                    }
                    if (key is int index)
                    {
                        if (index == sequencePrefix + 1)
                        {
                            sequencePrefix = index;
                            sequenceEndKnown = false;
                        }
                    }
                    else if (key is not string)
                    {
                        sequencePrefix = 0;
                        sequenceEndKnown = false;
                    }
                }
            }
        }
    }

    public sealed partial class ScriptVm
    {
        private const int MinimumCachedInteger = -16;
        private const int MaximumCachedInteger = 255;
        private static readonly object[] CachedIntegers = CreateCachedIntegers();

        private static object[] CreateCachedIntegers()
        {
            var integers = new object[MaximumCachedInteger - MinimumCachedInteger + 1];
            for (int i = 0; i < integers.Length; i++) integers[i] = i + MinimumCachedInteger;
            return integers;
        }

        internal static object BoxInteger(int value)
        {
            uint index = unchecked((uint)(value - MinimumCachedInteger));
            return index <= MaximumCachedInteger - MinimumCachedInteger ? CachedIntegers[index] : (object)value;
        }

        public System.Threading.CancellationToken Cancellation { get; set; }
        public delegate object?[] Native(object?[] args);
        internal sealed class Cell
        {
            private object? value;
            public string? Origin;
            public object? Value
            {
                get => value;
                set
                {
                    this.value = value;
                    Origin = null;
                }
            }

            public void CopyFrom(Cell source)
            {
                string? origin = source.Origin;
                Value = source.Value;
                Origin = origin;
            }

            public bool TryNormalizeNumber(out int number)
            {
                if (value is int integer)
                {
                    number = integer;
                    return true;
                }
                if (!TryNumber(value, out number))
                {
                    return false;
                }
                Value = BoxInteger(number);
                return true;
            }
        }

        private struct RegisterCaptures
        {
            private const int RegistersPerWord = 64;
            private const int RegisterOperandLimit = 1 << 9;
            private ulong firstWord;
            private ulong[]? remainingWords;

            internal void Mark(int register)
            {
                int word = register / RegistersPerWord;
                ulong bit = 1UL << (register % RegistersPerWord);
                if (word == 0)
                {
                    firstWord |= bit;
                }
                else
                {
                    remainingWords ??= new ulong[RegisterOperandLimit / RegistersPerWord - 1];
                    remainingWords[word - 1] |= bit;
                }
            }

            internal bool HasOpenCaptures
            {
                get
                {
                    if (firstWord != 0) return true;
                    if (remainingWords != null)
                    {
                        foreach (ulong word in remainingWords)
                            if (word != 0) return true;
                    }
                    return false;
                }
            }

            internal void CloseFrom(int first, Cell[] registers)
            {
                int firstAffectedWord = first / RegistersPerWord;
                if (firstAffectedWord == 0 && firstWord != 0)
                {
                    firstWord = CloseWord(firstWord, first, 0, registers);
                }
                if (remainingWords == null) return;
                for (int word = Math.Max(1, firstAffectedWord); word <= remainingWords.Length; word++)
                {
                    ulong captured = remainingWords[word - 1];
                    if (captured != 0) remainingWords[word - 1] = CloseWord(captured, first, word * RegistersPerWord, registers);
                }
            }

            private static ulong CloseWord(ulong captured, int first, int wordStart, Cell[] registers)
            {
                int skip = Math.Max(0, first - wordStart);
                ulong remaining = captured >> skip;
                for (int register = wordStart + skip; remaining != 0; register++, remaining >>= 1)
                {
                    if ((remaining & 1) != 0)
                    {
                        // The closure retains the old cell when the register is reused.
                        Cell cell = registers[register];
                        registers[register] = new Cell { Value = cell.Value, Origin = cell.Origin };
                    }
                }
                return captured & ((1UL << skip) - 1);
            }
        }

        internal sealed class Closure
        {
            public readonly ScriptPrototype Prototype;
            public readonly Cell[] Upvalues;
            public ScriptTable Environment;

            public Closure(ScriptPrototype prototype, Cell[] upvalues, ScriptTable environment)
            {
                Prototype = prototype;
                Upvalues = upvalues;
                Environment = environment;
            }
        }

        private ScriptTable globals = new ScriptTable(0, 4);
        public ScriptTable Globals
        {
            get => globals;
            internal set
            {
                Account(value);
                globals = value;
            }
        }
        public long Instructions
        {
            get; private set;
        }
        public int Budget { get; set; } = 1000000;
        internal Func<string, object>? InternString;

        private object? CanonicalString(object? value)
        {
            object? canonical = value is string text && InternString != null ? InternString(text) : value;
            return Account(canonical);
        }
        internal const int StackLimit = 2048;
        private long remaining;
        private int depth;
        private int nativeBoundaryDepth;
        private readonly List<Cell[]> activeRegisterFrames = new List<Cell[]>();
        private const int MaximumCachedRegisters = 32;
        private Cell[]?[]? cachedRegisterFrames;

        private Cell[] GetRegisterFrame(int count, bool cacheable)
        {
            if (cacheable && cachedRegisterFrames?[count] is Cell[] cached)
            {
                cachedRegisterFrames[count] = null;
                return cached;
            }
            var registers = new Cell[count];
            for (int index = 0; index < count; index++)
            {
                registers[index] = new Cell();
            }
            return registers;
        }

        private void CacheRegisterFrame(Cell[] registers)
        {
            var cache = cachedRegisterFrames ??= new Cell[MaximumCachedRegisters + 1][];
            if (cache[registers.Length] != null)
            {
                return; // A nested call already returned a frame of the same size.
            }
            foreach (Cell cell in registers)
            {
                cell.Value = null;
            }
            cache[registers.Length] = registers;
        }

        public ScriptVm()
        {
            InitializeAllocations();
            RegisterMetatables();
            RegisterEnvironments();
            RegisterCoroutines();
            Globals["_next"] = new Native(IterateTable);
        }

        internal static object?[] IterateTable(object?[] args)
        {
            if (args.Length == 0 || !(args[0] is ScriptTable table))
            {
                throw new RuntimeFault("table iterator requires a table");
            }
            return table.TryNext(args.Length > 1 ? args[1] : null, out object? key, out object? value)
                ? new[] { key, value } : new object?[] { null };
        }

        public object?[] Execute(ScriptPrototype prototype)
        {
            return Call(Load(prototype), Array.Empty<object?>());
        }

        public object Load(ScriptPrototype prototype)
        {
            if (prototype.Upvalues != 0)
            {
                throw new RuntimeFault("script: top-level chunk has unbound upvalues");
            }
            Account(prototype);
            if (InternString != null)
            {
                activeNativeArguments.Add(new object?[] { prototype });
                try
                {
                    var prototypes = new Stack<ScriptPrototype>();
                    prototypes.Push(prototype);
                    while (prototypes.Count != 0)
                    {
                        ScriptPrototype current = prototypes.Pop();
                        if (current.SharedDebugStrings == null)
                        {
                            current.SharedDebugStrings = new object?[1 + current.Locals.Length + current.UpvalueNames.Length];
                            current.SharedDebugStrings[0] = current.Source;
                            for (int index = 0; index < current.Locals.Length; index++)
                            {
                                current.SharedDebugStrings[index + 1] = current.Locals[index].Name;
                            }
                            for (int index = 0; index < current.UpvalueNames.Length; index++)
                            {
                                current.SharedDebugStrings[1 + current.Locals.Length + index] = current.UpvalueNames[index];
                            }
                        }
                        for (int index = 0; index < current.SharedDebugStrings.Length; index++)
                        {
                            current.SharedDebugStrings[index] = CanonicalString(current.SharedDebugStrings[index]);
                        }
                        for (int index = 0; index < current.Constants.Length; index++)
                        {
                            current.Constants[index] = CanonicalString(current.Constants[index]);
                        }
                        foreach (ScriptPrototype child in current.Children)
                        {
                            prototypes.Push(child);
                        }
                    }
                }
                finally
                {
                    activeNativeArguments.RemoveAt(activeNativeArguments.Count - 1);
                }
            }
            object closure = new Closure(prototype, Array.Empty<Cell>(), Globals);
            Account(closure);
            return closure;
        }

        internal static ScriptPrototype? FunctionPrototype(object? function)
        {
            return function is Closure closure ? closure.Prototype : null;
        }

        public static int Number(object? value)
        {
            if (TryNumber(value, out int integer))
            {
                return integer;
            }
            throw new RuntimeFault($"script: expected integer, got {value ?? "nil"}");
        }

        internal static bool IsFunction(object? value)
        {
            return value is Native || value is Closure;
        }

        internal ScriptTable FunctionEnvironment(object? function)
        {
            return function is Closure closure ? closure.Environment : Globals;
        }

        internal bool SetFunctionEnvironment(object? function, ScriptTable environment)
        {
            if (!(function is Closure closure))
            {
                return false;
            }
            closure.Environment = environment;
            return true;
        }

        public static string Text(object? value)
        {
            return value is string text ? text : value is ScriptMutableString mutable ? mutable.Text :
                value is int number ? number.ToString(CultureInfo.InvariantCulture)
                : throw new RuntimeFault("script: expected string or number");
        }

        public static bool IsString(object? value) => value is string || value is ScriptMutableString;

        internal static bool Truth(object? value)
        {
            return value != null && !(value is bool boolean && !boolean);
        }

        public object?[] Call(object? function, params object?[] args)
        {
            if (depth >= 64)
            {
                throw new RuntimeFault("script call depth exceeded");
            }
            if (depth == 0)
            {
                remaining = Budget;
            }
            depth++;
            int environmentFrame = -1;
            if (IsFunction(function))
            {
                environmentFrame = environmentFrameCount++;
                environmentFunctions[environmentFrame] = function;
                environmentTailCalls[environmentFrame] = 0;
                environmentInstructions[environmentFrame] = 0;
            }
            activeNativeArguments.Add(args);
            int argumentFrame = activeNativeArguments.Count - 1;
            Account(function);
            int allocationTop = EnterAllocationCall(function, args.Length);
            bool completed = false;
            try
            {
                for (int index = 0; index < args.Length; index++)
                {
                    args[index] = CanonicalString(args[index]);
                }
                if (function is Native native)
                {
                    nativeBoundaryDepth++;
                    try
                    {
                        object?[] values = native(args);
                        activeNativeArguments.Add(values);
                        try
                        {
                            for (int index = 0; index < values.Length; index++)
                            {
                                values[index] = CanonicalString(values[index]);
                            }
                            return CompleteCall(values, argumentFrame, environmentFrame, allocationTop, ref completed);
                        }
                        finally
                        {
                            activeNativeArguments.RemoveAt(activeNativeArguments.Count - 1);
                        }
                    }
                    finally
                    {
                        nativeBoundaryDepth--;
                    }
                }
                if (function is Closure closure)
                {
                    return CompleteCall(Run(closure, args), argumentFrame, environmentFrame, allocationTop, ref completed);
                }
                object? method = Metatable(function)?["__call"];
                if (IsFunction(method))
                {
                    var arguments = new object?[args.Length + 1];
                    arguments[0] = function;
                    Array.Copy(args, 0, arguments, 1, args.Length);
                    return CompleteCall(Call(method, arguments), argumentFrame, environmentFrame, allocationTop, ref completed);
                }
                throw new RuntimeFault($"script: attempt to call {function ?? "nil"}");
            }
            finally
            {
                activeNativeArguments.RemoveAt(activeNativeArguments.Count - 1);
                if (!completed)
                {
                    CloseCallFrame(environmentFrame, allocationTop);
                }
            }
        }

        private void CloseCallFrame(int environmentFrame, int allocationTop)
        {
            if (environmentFrame >= 0)
            {
                environmentFunctions[environmentFrame] = null;
                environmentTailCalls[environmentFrame] = 0;
                environmentInstructions[environmentFrame] = 0;
                environmentFrameCount = environmentFrame;
            }
            depth--;
            LeaveAllocationCall(allocationTop);
            // A callback may close the session before its results and frames unwind.
            if (depth == 0 && releaseAllocationsOnReturn) ReleaseAllocations();
        }

        private object?[] CompleteCall(object?[] results, int argumentFrame, int environmentFrame,
            int allocationTop, ref bool completed)
        {
            // Reference GC runs after poscall: returned values replace the callee and args.
            activeNativeArguments[argumentFrame] = results;
            CloseCallFrame(environmentFrame, allocationTop);
            completed = true;
            CollectionRequested?.Invoke();
            return results;
        }

        private object?[] Run(Closure closure, object?[] args)
        {
            while (true)
            {
                object?[] results = RunFrame(closure, args, out Closure? next, out object?[] nextArguments);
                if (next == null)
                {
                    return results;
                }
                closure = next;
                args = nextArguments;
                environmentFunctions[environmentFrameCount - 1] = closure;
                environmentTailCalls[environmentFrameCount - 1]++;
            }
        }

        private object?[] RunFrame(Closure closure, object?[] args, out Closure? tail, out object?[] tailArguments,
            CoroutineFrame? resumedFrame = null, object?[]? resumedValues = null)
        {
            tail = null;
            tailArguments = Array.Empty<object?>();
            var prototype = closure.Prototype;
            int registerCount = prototype.Stack;
            // A captured or suspended frame can outlive this call.
            bool cacheRegisters = resumedFrame == null && currentCoroutine == null &&
                prototype.Children.Length == 0 && (uint)registerCount <= MaximumCachedRegisters;
            var registers = resumedFrame?.Registers ?? GetRegisterFrame(registerCount, cacheRegisters);
            RegisterCaptures openCaptures = resumedFrame is CapturedCoroutineFrame captured ? captured.Captures : default;
            if (resumedFrame == null)
            {
                int parameters = Math.Min(registerCount, Math.Min(prototype.Parameters, args.Length));
                for (int i = 0; i < parameters; i++)
                {
                    registers[i].Value = args[i];
                }
            }
            Cell Register(int index) => CheckedRegister(registers, prototype, index);
            void GrowOpenStack(int required)
            {
                if (required < 0 || required > StackLimit)
                {
                    throw new RuntimeFault("script open-result stack limit");
                }
                int previous = registers.Length;
                if (required > previous)
                {
                    EnsureAllocationStack(CurrentAllocationThread().Top + required - previous);
                    Array.Resize(ref registers, required);
                    activeRegisterFrames[activeRegisterFrames.Count - 1] = registers;
                    for (int i = previous; i < required; i++)
                    {
                        registers[i] = new Cell();
                    }
                }
            }
            object? OpenValue(int index)
            {
                return index >= 0 && index < registers.Length ? registers[index].Value
                    : throw new RuntimeFault($"invalid open stack index {index}");
            }
            object? Constant(int index)
            {
                return index >= 0 && index < prototype.Constants.Length ? prototype.Constants[index]
                    : throw new RuntimeFault($"invalid constant {index}");
            }
            object? Operand(int index)
            {
                return index < 250 ? Register(index).Value : Constant(index - 250);
            }
            ScriptTable Table(object? value)
            {
                return value as ScriptTable ?? throw new RuntimeFault("expected table");
            }
            if (resumedFrame == null && prototype.Vararg)
            {
                int extraCount = Math.Max(0, args.Length - prototype.Parameters);
                var extras = NewTable(extraCount, 1);
                for (int i = 0; i < extraCount; i++)
                {
                    extras[i + 1] = args[prototype.Parameters + i];
                }
                extras["n"] = extraCount;
                Register(prototype.Parameters).Value = extras;
            }
            int pc = resumedFrame?.Pc ?? 0;
            int current = 0;
            int top = resumedFrame?.Top ?? prototype.Stack;
            bool yieldCaptured = false;
            // Script closures copy their arguments into the callee frame. Native callbacks can retain the array.
            object?[]? singleCallArguments = null;
            object?[]? pairCallArguments = null;
            void WriteCallResults(int first, int countField, object?[] values)
            {
                int resultCount = countField == 0 ? values.Length : countField - 1;
                if (countField == 0)
                {
                    GrowOpenStack(checked(first + resultCount));
                }
                for (int i = 0; i < resultCount; i++)
                {
                    Cell resultRegister = countField == 0 ? registers[first + i] : Register(first + i);
                    resultRegister.Value = i < values.Length ? values[i] : null;
                }
                top = countField == 0 ? first + resultCount : prototype.Stack;
            }
            activeRegisterFrames.Add(registers);
            try
            {
                Account(closure);
                if (resumedFrame != null)
                {
                    if (resumedFrame.TailCall)
                    {
                        return resumedValues ?? Array.Empty<object?>();
                    }
                    WriteCallResults(resumedFrame.ResultBase, resumedFrame.ResultField,
                        resumedValues ?? Array.Empty<object?>());
                }
                // Nested calls use their own environment slots and restore this frame on return.
                ref int activeInstruction = ref environmentInstructions[environmentFrameCount - 1];
                uint[] instructions;
                while (unchecked((uint)pc) < (uint)(instructions = prototype.Code).Length)
                {
                    if ((remaining & 4095) == 0)
                    {
                        Cancellation.ThrowIfCancellationRequested();
                    }
                    if (--remaining < 0)
                    {
                        throw new RuntimeFault("script instruction budget exhausted");
                    }
                    Instructions++;
                    current = pc;
                    activeInstruction = current;
                    uint instruction = instructions[pc++];
                    int op = (int)(instruction & 63);
                    int a = (int)(instruction >> 24);
                    int b = (int)(instruction >> 15 & 511);
                    int c = (int)(instruction >> 6 & 511);
                    int bx = (int)(instruction >> 6 & 262143);
                    switch (op)
                    {
                        case 0:
                            Register(a).CopyFrom(Register(b));
                            break;
                        case 1:
                            Register(a).Value = Constant(bx);
                            break;
                        case 2:
                            Register(a).Value = ScriptBoolean.FromField(b);
                            pc += c != 0 ? 1 : 0;
                            break;
                        case 3:
                            for (int i = Math.Min(a, b); i <= b; i++)
                            {
                                Register(i).Value = null;
                            }
                            break;
                        case 4:
                            Register(a).CopyFrom(Upvalue(closure, b));
                            break;
                        case 5:
                            Cell globalTarget = Register(a);
                            object? global = Constant(bx);
                            globalTarget.Value = GetValue(closure.Environment, global);
                            if (globalTarget.Value == null)
                            {
                                globalTarget.Origin = $"global '{Text(global)}'";
                            }
                            break;
                        case 6:
                            object? key = Operand(c);
                            Cell fieldTarget = Register(a);
                            fieldTarget.Value = GetValue(Register(b).Value, key);
                            if (fieldTarget.Value == null)
                            {
                                fieldTarget.Origin = $"table key '{key ?? "nil"}'";
                            }
                            break;
                        case 7:
                            SetValue(closure.Environment, Constant(bx), Register(a).Value);
                            break;
                        case 8:
                            Upvalue(closure, b).CopyFrom(Register(a));
                            break;
                        case 9:
                            SetValue(Register(a).Value, Operand(b), Operand(c));
                            break;
                        case 10:
                            Register(a).Value = NewTable((b & 7) << (b >> 3), c);
                            CollectionRequested?.Invoke();
                            break;
                        case 11:
                            Register(a + 1).Value = Register(b).Value;
                            object? method = Operand(c);
                            Cell methodTarget = Register(a);
                            methodTarget.Value = GetValue(Register(b).Value, method);
                            if (methodTarget.Value == null)
                            {
                                methodTarget.Origin = $"table key '{method ?? "nil"}'";
                            }
                            break;
                        case 12:
                        case 13:
                        case 14:
                        case 15:
                        case 16:
                            Register(a).Value = Arithmetic(op, Operand(b), Operand(c));
                            break;
                        case 36:
                        case 37:
                        case 38:
                            if (Operand(b) is int left && Operand(c) is int right)
                            {
                                Register(a).Value = BoxInteger(op switch
                                {
                                    36 => left & right,
                                    37 => left | right,
                                    _ => left ^ right
                                });
                            }
                            break;
                        case 17:
                            object? operand = Register(b).Value;
                            Register(a).Value = TryNumber(operand, out int numeric)
                                ? BoxInteger(unchecked(-numeric)) : BinaryMethod("__unm", operand, null);
                            break;
                        case 18:
                            Register(a).Value = ScriptBoolean.Box(!Truth(Register(b).Value));
                            break;
                        case 19:
                            Register(a).Value = Concatenate(b, c, registers, prototype);
                            break;
                        case 20:
                            pc += bx - 131071;
                            break;
                        case 21:
                        case 22:
                        case 23:
                            object? l = Operand(b);
                            object? r = Operand(c);
                            bool comparison;
                            if (op == 21)
                            {
                                comparison = EqualValue(l, r);
                            }
                            else
                            {
                                comparison = CompareValue(l, r, op == 23);
                            }
                            pc += (comparison ? 1 : 0) == a ? 0 : 1;
                            break;
                        case 24:
                            if ((Truth(Register(b).Value) ? 0 : 1) != c)
                            {
                                Register(a).CopyFrom(Register(b));
                            }
                            else
                            {
                                pc++;
                            }
                            break;
                        case 25:
                        case 26:
                            int argumentCount = b == 0 ? top - a - 1 : b - 1;
                            if (argumentCount < 0 || argumentCount > StackLimit)
                            {
                                throw new RuntimeFault("invalid argument count");
                            }
                            Cell? target = a < prototype.Stack ? registers[a] : null;
                            object? called = target?.Value;
                            bool reuseArguments = op == 25 && argumentCount is 1 or 2 && called is Closure;
                            var arguments = argumentCount switch
                            {
                                0 => Array.Empty<object?>(),
                                1 when reuseArguments => singleCallArguments ??= new object?[1],
                                2 when reuseArguments => pairCallArguments ??= new object?[2],
                                _ => new object?[argumentCount]
                            };
                            for (int i = 0; i < arguments.Length; i++)
                            {
                                arguments[i] = b == 0 ? OpenValue(a + i + 1) : Register(a + i + 1).Value;
                            }
                            if (called == null)
                            {
                                string? origin = (target ?? Register(a)).Origin;
                                throw new RuntimeFault($"script: attempt to call nil ({origin ?? "unknown origin"}, r{a})");
                            }
                            if (op == 26)
                            {
                                if (!IsFunction(called))
                                {
                                    object? callMethod = Metatable(called)?["__call"];
                                    if (IsFunction(callMethod))
                                    {
                                        var withSelf = new object?[arguments.Length + 1];
                                        withSelf[0] = called;
                                        Array.Copy(arguments, 0, withSelf, 1, arguments.Length);
                                        arguments = withSelf;
                                        called = callMethod;
                                    }
                                }
                                if (called is Closure next)
                                {
                                    tail = next;
                                    tailArguments = arguments;
                                    return Array.Empty<object?>();
                                }
                            }
                            object?[] results;
                            try
                            {
                                results = Call(called, arguments);
                            }
                            catch (CoroutineYield signal)
                            {
                                signal.Frames.Add(openCaptures.HasOpenCaptures
                                    ? new CapturedCoroutineFrame(closure, registers, pc, top, a, c, op == 26, openCaptures)
                                    : new CoroutineFrame(closure, registers, pc, top, a, c, op == 26));
                                yieldCaptured = true;
                                throw;
                            }
                            if (reuseArguments)
                            {
                                Array.Clear(arguments, 0, argumentCount);
                            }
                            WriteCallResults(a, c, results);
                            break;
                        case 27:
                            Register(a);
                            int returned = b == 0 ? top - a : b - 1;
                            if (returned < 0 || returned > StackLimit)
                            {
                                throw new RuntimeFault("invalid return count");
                            }
                            var values = returned == 0 ? Array.Empty<object?>() : new object?[returned];
                            for (int i = 0; i < returned; i++)
                            {
                                values[i] = b == 0 ? OpenValue(a + i) : Register(a + i).Value;
                            }
                            return values;
                        case 28:
                            if (!(Register(a).Value is int previousIndex))
                            {
                                throw new RuntimeFault("for initial value must be a number");
                            }
                            if (!Register(a + 1).TryNormalizeNumber(out int limit))
                            {
                                throw new RuntimeFault("for limit must be a number");
                            }
                            if (!Register(a + 2).TryNormalizeNumber(out int step))
                            {
                                throw new RuntimeFault("for step must be a number");
                            }
                            int index = unchecked(previousIndex + step);
                            if (step > 0 ? index <= limit : index >= limit)
                            {
                                Register(a).Value = BoxInteger(index);
                                pc += bx - 131071;
                            }
                            break;
                        case 29:
                            int iteratorBase = a + c + 3;
                            Register(iteratorBase).CopyFrom(Register(a));
                            Register(iteratorBase + 1).CopyFrom(Register(a + 1));
                            Register(iteratorBase + 2).CopyFrom(Register(a + 2));
                            object? iterator = Register(iteratorBase).Value;
                            bool scriptIterator = iterator is Closure;
                            object?[] iteratorArguments = scriptIterator
                                ? (pairCallArguments ??= new object?[2]) : new object?[2];
                            iteratorArguments[0] = Register(iteratorBase + 1).Value;
                            iteratorArguments[1] = Register(iteratorBase + 2).Value;
                            object?[] iterated = Call(iterator, iteratorArguments);
                            if (scriptIterator)
                            {
                                iteratorArguments[0] = null;
                                iteratorArguments[1] = null;
                            }
                            GrowOpenStack(iteratorBase + c + 1);
                            for (int i = 0; i <= c; i++)
                            {
                                object? result = i < iterated.Length ? iterated[i] : null;
                                registers[iteratorBase + i].Value = result;
                                registers[a + 2 + i].Value = result;
                            }
                            top = prototype.Stack;
                            if (Register(a + 2).Value == null)
                            {
                                pc++;
                            }
                            break;
                        case 30:
                            if (Register(a).Value is ScriptTable)
                            {
                                Register(a + 1).Value = Register(a).Value;
                                Register(a).Value = Globals["_next"];
                            }
                            pc += bx - 131071;
                            break;
                        case 31:
                        case 32:
                            var table = Table(Register(a).Value);
                            int blockStart = bx & ~31;
                            int items = op == 31 ? bx - blockStart + 1 : top - a - 1;
                            for (int i = 1; i <= items; i++)
                            {
                                table[BoxInteger(blockStart + i)] = op == 32 ? OpenValue(a + i) : Register(a + i).Value;
                            }
                            if (op == 32)
                            {
                                top = prototype.Stack;
                            }
                            break;
                        case 33:
                            if (!cacheRegisters) openCaptures.CloseFrom(a, registers);
                            break;
                        case 34:
                            // A host callback can replace the publicly mutable prototype.
                            cacheRegisters = false;
                            if (bx >= prototype.Children.Length)
                            {
                                throw new RuntimeFault("invalid child prototype");
                            }
                            var child = prototype.Children[bx];
                            var captures = new Cell[child.Upvalues];
                            for (int i = 0; i < captures.Length; i++)
                            {
                                if (pc >= prototype.Code.Length)
                                {
                                    throw new RuntimeFault("truncated closure captures");
                                }
                                uint capture = prototype.Code[pc++];
                                int source = (int)(capture >> 15 & 511);
                                switch (capture & 63)
                                {
                                    case 0:
                                        captures[i] = Register(source);
                                        openCaptures.Mark(source);
                                        break;
                                    case 4:
                                        captures[i] = Upvalue(closure, source);
                                        break;
                                    default:
                                        throw new RuntimeFault("invalid closure capture");
                                }
                            }
                            Register(a).Value = new Closure(child, captures, closure.Environment);
                            Account(Register(a).Value);
                            CollectionRequested?.Invoke();
                            break;
                        case 35:
                            if (Register(b).Value is int complement)
                            {
                                Register(a).Value = BoxInteger(~complement);
                            }
                            break;
                        default:
                            throw new RuntimeFault($"unsupported script opcode {op}");
                    }
                }
                throw new RuntimeFault("script PC left code without RETURN");
            }
            catch (CoroutineYield)
            {
                if (!yieldCaptured)
                {
                    throw new RuntimeFault("yield across unsupported call boundary");
                }
                throw;
            }
            catch (RuntimeFault error)
            {
                int line = current >= 0 && current < prototype.Lines.Length ? prototype.Lines[current] : 0;
                string source = prototype.CurrentSource;
                string location = line > 0 ? $"{source}:{line}" : source;
                throw new RuntimeFault($"{location} instruction {current}: {error.Message}");
            }
            finally
            {
                activeRegisterFrames.RemoveAt(activeRegisterFrames.Count - 1);
                if (cacheRegisters && !yieldCaptured && registers.Length == registerCount)
                {
                    CacheRegisterFrame(registers);
                }
            }
        }

        private static Cell CheckedRegister(Cell[] registers, ScriptPrototype prototype, int index)
        {
            return index >= 0 && index < prototype.Stack ? registers[index]
                : throw new RuntimeFault($"invalid register {index}");
        }

        private static Cell Upvalue(Closure closure, int index)
        {
            return index < closure.Upvalues.Length ? closure.Upvalues[index]
                : throw new RuntimeFault("invalid upvalue index");
        }
    }
}

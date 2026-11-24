using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private PatternBuffers? cachedPatternBuffers;
        private PatternIteratorOwner? patternIteratorOwner;
        private ConditionalWeakTable<ScriptVm.Native, PatternIterator>? mutablePatternIterators;

        private sealed class PatternIteratorOwner
        {
            internal RuntimeSession? Session;
            internal int Budget;
        }

        private sealed class PatternIterator
        {
            private readonly PatternIteratorOwner owner;
            internal readonly object?[] Inputs;
            private int next;

            internal PatternIterator(PatternIteratorOwner owner, object? text, object? pattern)
            {
                this.owner = owner;
                Inputs = new[] { text, pattern };
            }

            internal object?[] Advance(object?[] _) =>
                Inputs[0] is ScriptMutableString mutable ? Advance(new PatternInput<byte>(mutable)) :
                Advance(new PatternInput<char>(ScriptVm.Text(Inputs[0])));

            private object?[] Advance<TText>(PatternInput<TText> text) where TText : unmanaged =>
                Inputs[1] is ScriptMutableString mutable ? Advance(text, new PatternInput<byte>(mutable)) :
                Advance(text, new PatternInput<char>(ScriptVm.Text(Inputs[1])));

            private object?[] Advance<TText, TPattern>(PatternInput<TText> text, PatternInput<TPattern> pattern)
                where TText : unmanaged where TPattern : unmanaged
            {
                RuntimeSession? session = owner.Session;
                PatternBuffers buffers = session?.RentPatternBuffers() ?? new PatternBuffers();
                int budget = session == null ? owner.Budget : session.Script.Budget;
                var matcher = new BytePattern<TText, TPattern>(text, pattern, budget, buffers);
                try
                {
                    int candidate = matcher.FindNext(next, out int end);
                    if (candidate < 0) return Array.Empty<object?>();
                    next = end > candidate ? end : end + 1;
                    return matcher.Values(candidate, end);
                }
                finally
                {
                    session?.ReturnPatternBuffers(buffers);
                }
            }

            internal void SnapshotInputs(ref Dictionary<ScriptMutableString, string>? snapshots)
            {
                for (int i = 0; i < Inputs.Length; i++)
                {
                    if (!(Inputs[i] is ScriptMutableString mutable)) continue;
                    snapshots ??= new Dictionary<ScriptMutableString, string>();
                    if (!snapshots.TryGetValue(mutable, out string? snapshot))
                    {
                        snapshot = mutable.Text;
                        snapshots.Add(mutable, snapshot);
                    }
                    Inputs[i] = snapshot;
                }
            }
        }

        private PatternBuffers RentPatternBuffers()
        {
            PatternBuffers buffers = cachedPatternBuffers ?? new PatternBuffers();
            cachedPatternBuffers = null;
            return buffers;
        }

        private void ReturnPatternBuffers(PatternBuffers buffers)
        {
            buffers.Choices?.Clear();
            if (!disposed) cachedPatternBuffers = buffers;
        }

        private void RegisterStringPatterns(ScriptTable library)
        {
            library["find"] = new ScriptVm.Native(args =>
            {
                RequireScriptArguments(args, 2);
                string text = ScriptVm.Text(args[0]);
                string pattern = ScriptVm.Text(args[1]);
                int offset = args.Length > 2 && args[2] != null ? ScriptVm.Number(args[2]) : 1;
                long position = offset < 0 ? (long)text.Length + offset : (long)offset - 1;
                int start = (int)Math.Max(0, Math.Min(text.Length, position));
                if (args.Length < 4 || !ScriptVm.Truth(args[3]) || !HasPatternSyntax(pattern))
                {
                    int found = text.IndexOf(pattern, start, StringComparison.Ordinal);
                    return found < 0 ? new object?[] { null } : new object?[] { found + 1, found + pattern.Length };
                }
                bool anchored = pattern[0] == '^';
                PatternBuffers buffers = RentPatternBuffers();
                var matcher = new BytePattern<char, char>(new PatternInput<char>(text),
                    new PatternInput<char>(pattern), Script.Budget, buffers);
                try
                {
                    int end;
                    int candidate = anchored
                        ? (matcher.Try(start, 1, out end) ? start : -1)
                        : matcher.FindNext(start, out end);
                    return candidate < 0 ? new object?[] { null } :
                        matcher.Values(candidate, end, includePositions: true);
                }
                finally
                {
                    ReturnPatternBuffers(buffers);
                }
            });
            library["findEx"] = new ScriptVm.Native(CreatePatternIterator);
            library["findex"] = library["findEx"];
            library["subEx"] = new ScriptVm.Native(ReplaceScriptPattern);
            library["subex"] = library["subEx"];
        }

        private object?[] CreatePatternIterator(object?[] args)
        {
            RequireScriptArguments(args, 2);
            ValidatePatternInput(args[0]);
            ValidatePatternInput(args[1]);
            var owner = patternIteratorOwner ??= new PatternIteratorOwner { Session = disposed ? null : this, Budget = Script.Budget };
            var state = new PatternIterator(owner, args[0], args[1]);
            ScriptVm.Native iterator = state.Advance;
            if (args[0] is ScriptMutableString || args[1] is ScriptMutableString)
            {
                if (owner.Session != null)
                {
                    (mutablePatternIterators ??= new ConditionalWeakTable<ScriptVm.Native, PatternIterator>()).Add(iterator, state);
                }
                else
                {
                    Dictionary<ScriptMutableString, string>? snapshots = null;
                    state.SnapshotInputs(ref snapshots);
                }
            }
            Script.RetainNativeValues(iterator, state.Inputs);
            return new object?[] { iterator };
        }

        private static void ValidatePatternInput(object? value)
        {
            if (value is ScriptMutableString mutable)
            {
                mutable.Memory.Check(mutable.Pointer, (uint)mutable.Length);
            }
            else if (value is not (string or int))
            {
                throw new RuntimeFault("script: expected string or number");
            }
        }

        private void ClosePatternIterators()
        {
            if (patternIteratorOwner == null) return;
            patternIteratorOwner.Budget = Script.Budget;
            patternIteratorOwner.Session = null;
            if (mutablePatternIterators != null)
            {
                Dictionary<ScriptMutableString, string>? snapshots = null;
                foreach (var entry in mutablePatternIterators) entry.Value.SnapshotInputs(ref snapshots);
                mutablePatternIterators = null;
            }
        }

        private static bool HasPatternSyntax(string pattern)
        {
            int zero = pattern.IndexOf('\0');
            ReadOnlySpan<char> prefix = zero < 0 ? pattern.AsSpan() : pattern.AsSpan(0, zero);
            return prefix.IndexOfAny("^$*+?.([%-".AsSpan()) >= 0;
        }

        private const int RequiredDigit = char.MaxValue + 1;

        private static bool IsAsciiDigit(char character) => (uint)(character - '0') <= 9;

        private static bool RejectsStartCharacter(char character, int requiredStart) =>
            requiredStart == RequiredDigit ? !IsAsciiDigit(character) : character != requiredStart;

        private object?[] ReplaceScriptPattern(object?[] args)
        {
            RequireScriptArguments(args, 3);
            string text = ScriptVm.Text(args[0]);
            string pattern = ScriptVm.Text(args[1]);
            object? replacement = args[2];
            bool function = ScriptVm.IsFunction(replacement);
            string template = function ? "" : ScriptVm.Text(replacement);
            bool literalTemplate = template.IndexOf('%') < 0;
            int maximum = args.Length > 3 && args[3] != null ? ScriptVm.Number(args[3]) : text.Length + 1;
            bool anchored = pattern.StartsWith("^", StringComparison.Ordinal);
            StringBuilder? output = null;
            Span<char> positionText = stackalloc char[11];
            void RequireOutputSpace(int length)
            {
                if ((long)(output?.Length ?? 0) + length > Package.MaxBytes)
                {
                    throw new RuntimeFault("string pattern output size limit");
                }
            }
            void Append(ReadOnlySpan<char> value)
            {
                RequireOutputSpace(value.Length);
                (output ??= new StringBuilder()).Append(value);
            }
            int position = 0;
            int copiedThrough = 0;
            int count = 0;
            PatternBuffers buffers = RentPatternBuffers();
            var matcher = new BytePattern<char, char>(new PatternInput<char>(text),
                new PatternInput<char>(pattern), Script.Budget, buffers);
            try
            {
                var (requiredStart, rejectionCost, bracketStart) = matcher.RequiredStart();
                while (count < maximum)
                {
                    if (position < text.Length && (bracketStart >= 0 ||
                        (requiredStart >= 0 && RejectsStartCharacter(text[position], requiredStart))))
                    {
                        // Leave any attempt that could cross the output limit to the normal matcher.
                        int capacity = Package.MaxBytes - (output?.Length ?? 0) - (position - copiedThrough);
                        int length = Math.Min(text.Length - position, capacity);
                        position += bracketStart >= 0
                            ? matcher.SkipRejectedBracketStarts(position, length, bracketStart, rejectionCost)
                            : matcher.SkipRejectedStarts(position, length, requiredStart, rejectionCost);
                    }
                    bool found = matcher.Try(position, anchored ? 1 : 0, out int end);
                    if (found)
                    {
                        Append(text.AsSpan(copiedThrough, position - copiedThrough));
                        copiedThrough = position;
                        count++;
                        if (function)
                        {
                            object?[] results = Script.Call(replacement, matcher.Values(position, end));
                            if (results.Length > 0 && (ScriptVm.IsString(results[0]) || results[0] is int))
                            {
                                Append(ScriptVm.Text(results[0]).AsSpan());
                            }
                        }
                        else if (literalTemplate)
                        {
                            Append(template.AsSpan());
                        }
                        else
                        {
                            for (int index = 0; index < template.Length; index++)
                            {
                                char character = template[index];
                                if (character == '%')
                                {
                                    index++;
                                    character = index < template.Length ? template[index] : '\0';
                                    if (character >= '0' && character <= '9')
                                    {
                                        var capture = matcher.CaptureRange(character - '1');
                                        if (capture.Length == -2)
                                        {
                                            (capture.Start + 1).TryFormat(positionText, out int written,
                                                provider: CultureInfo.InvariantCulture);
                                            Append(positionText.Slice(0, written));
                                        }
                                        else
                                        {
                                            Append(text.AsSpan(capture.Start, capture.Length));
                                        }
                                        continue;
                                    }
                                }
                                RequireOutputSpace(1);
                                output!.Append(character);
                            }
                        }
                    }
                    if (found && end > position)
                    {
                        position = end;
                        copiedThrough = end;
                    }
                    else if (position < text.Length)
                    {
                        position++;
                        RequireOutputSpace(position - copiedThrough);
                    }
                    else
                    {
                        break;
                    }
                    if (anchored)
                    {
                        break;
                    }
                }
                if (output == null)
                {
                    RequireOutputSpace(text.Length);
                    return new object?[] { text, 0 };
                }
                Append(text.AsSpan(copiedThrough));
                return new object?[] { output.ToString(), count };
            }
            finally
            {
                ReturnPatternBuffers(buffers);
            }
        }

        // Specialize indexing for byte and char inputs before entering the matcher.
        private readonly ref struct PatternInput<T> where T : unmanaged
        {
            private readonly string text;
            private readonly ReadOnlySpan<byte> bytes;

            internal PatternInput(string text)
            {
                this.text = text;
                bytes = default;
            }

            internal PatternInput(ScriptMutableString mutable)
            {
                text = string.Empty;
                bytes = mutable.Memory.ReadSpan(mutable.Pointer, (uint)mutable.Length);
            }

            internal int Length => typeof(T) == typeof(byte) ? bytes.Length : text.Length;
            internal ReadOnlySpan<char> Characters => text.AsSpan();
            internal ReadOnlySpan<byte> Bytes => bytes;
            internal char this[int index]
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => typeof(T) == typeof(byte) ? (char)bytes[index] : text[index];
            }
            internal string Substring(int start, int length) => typeof(T) == typeof(byte) ?
                ScriptChunk.Bytes.GetString(bytes.Slice(start, length)) : text.Substring(start, length);
            internal bool RangeEquals(int first, int second, int length) => typeof(T) == typeof(byte) ?
                bytes.Slice(first, length).SequenceEqual(bytes.Slice(second, length)) :
                string.CompareOrdinal(text, first, text, second, length) == 0;
        }

        private sealed class PatternBuffers
        {
            internal readonly List<(int Start, int Length)> Captures = new List<(int, int)>();
            internal Stack<PatternChoice>? Choices;
        }

        private struct PatternChoice
        {
            public int Next;
            public int Last;
            public int Step;
            public int Pattern;
            public int CaptureCount;
            public uint OpenCaptures;
        }

        // Byte-oriented, iterative backtracking. Only scratch buffers outlive the call.
        private ref struct BytePattern<TText, TPattern> where TText : unmanaged where TPattern : unmanaged
        {
            private const int CaptureLimit = sizeof(uint) * 8;
            private readonly PatternInput<TText> text;
            private readonly ReadOnlySpan<byte> patternBytes;
            private readonly ReadOnlySpan<char> patternCharacters;
            private readonly PatternBuffers buffers;
            private (int Atom, int Start, int Limit, int CharacterCost) repeatRun;
            private (int Start, int End, int Cost) lastBracket;
            private (int First, bool FirstMatch, int Second, bool SecondMatch, int Cost) bracketCharacters;
            private int remaining;

            internal BytePattern(PatternInput<TText> text, PatternInput<TPattern> pattern, int budget, PatternBuffers buffers)
            {
                this.text = text;
                if (typeof(TPattern) == typeof(byte))
                {
                    ReadOnlySpan<byte> value = pattern.Bytes;
                    int zero = value.IndexOf((byte)0);
                    patternBytes = zero < 0 ? value : value.Slice(0, zero);
                    patternCharacters = default;
                }
                else
                {
                    ReadOnlySpan<char> value = pattern.Characters;
                    int zero = value.IndexOf('\0');
                    patternCharacters = zero < 0 ? value : value.Slice(0, zero);
                    patternBytes = default;
                }
                this.buffers = buffers;
                remaining = Math.Max(1, budget);
                repeatRun = (-1, 0, 0, 0);
                lastBracket = (-1, 0, 0);
                bracketCharacters = (-1, false, -1, false, 0);
            }

            private void Work(int amount = 1)
            {
                if (amount > remaining)
                {
                    throw new RuntimeFault("string pattern work limit");
                }
                remaining -= amount;
            }

            internal (int Character, int Cost, int Bracket) RequiredStart()
            {
                int instruction = 0;
                int captures = 0;
                char atom = At(instruction);
                while (atom == '(')
                {
                    if (++captures > CaptureLimit) return (-1, 0, -1);
                    instruction += At(instruction + 1) == ')' ? 2 : 1;
                    atom = At(instruction);
                }
                // For a set, parsing and the character check are charged separately.
                if (atom == '[') return (-1, captures + 1, instruction);
                if (atom is '\0' or '^' or '$' or ')' or '.') return (-1, 0, -1);
                bool escaped = atom == '%';
                if (escaped && At(instruction + 1) != 'd') return (-1, 0, -1);
                if (At(instruction + (escaped ? 2 : 1)) is '*' or '-' or '?') return (-1, 0, -1);
                // Each leading capture adds one instruction before the atom and its character check.
                return (escaped ? RequiredDigit : atom, captures + 2, -1);
            }

            internal int FindNext(int start, out int end)
            {
                if (start <= text.Length)
                {
                    return Try(start, 0, out end) ? start : FindAfterFailedStart(start + 1, out end);
                }
                end = 0;
                return -1;
            }

            private int FindAfterFailedStart(int candidate, out int end)
            {
                int length = text.Length;
                var (requiredStart, rejectionCost, bracketStart) = RequiredStart();
                for (; candidate <= length; candidate++)
                {
                    if (requiredStart >= 0 && candidate < length &&
                        RejectsStartCharacter(text[candidate], requiredStart))
                    {
                        candidate += SkipRejectedStarts(candidate, length - candidate, requiredStart, rejectionCost);
                    }
                    else if (bracketStart >= 0 && candidate < length)
                    {
                        candidate += SkipRejectedBracketStarts(candidate, length - candidate, bracketStart, rejectionCost);
                    }
                    if (Try(candidate, 0, out end)) return candidate;
                }
                end = 0;
                return -1;
            }

            internal int SkipRejectedBracketStarts(int start, int length, int bracketStart, int prefixCost)
            {
                if (lastBracket.Start != bracketStart) return 0;
                int first = bracketCharacters.FirstMatch ? -1 : bracketCharacters.First;
                int second = bracketCharacters.SecondMatch ? -1 : bracketCharacters.Second;
                char value = text[start];
                if ((value != first && value != second) || At(lastBracket.End) is '*' or '-' or '?') return 0;

                int cost = prefixCost + lastBracket.Cost + bracketCharacters.Cost;
                int scanEnd = start + Math.Min(length, remaining / cost);
                int next = start;
                while (next < scanEnd)
                {
                    value = text[next];
                    if (value != first && value != second) break;
                    next++;
                }
                Work((next - start) * cost);
                return next - start;
            }

            internal int SkipRejectedStarts(int start, int length, int requiredStart, int rejectionCost)
            {
                length = Math.Min(length, remaining / rejectionCost);
                int skipped;
                if (requiredStart == RequiredDigit)
                {
                    skipped = 0;
                    if (typeof(TText) == typeof(byte))
                    {
                        ReadOnlySpan<byte> candidates = text.Bytes.Slice(start, length);
                        while (skipped < candidates.Length && !IsAsciiDigit((char)candidates[skipped])) skipped++;
                    }
                    else
                    {
                        ReadOnlySpan<char> candidates = text.Characters.Slice(start, length);
                        while (skipped < candidates.Length && !IsAsciiDigit(candidates[skipped])) skipped++;
                    }
                }
                else
                {
                    int match;
                    if (typeof(TText) == typeof(byte))
                    {
                        match = requiredStart <= byte.MaxValue
                            ? text.Bytes.Slice(start, length).IndexOf((byte)requiredStart) : -1;
                    }
                    else
                    {
                        match = text.Characters.Slice(start, length).IndexOf((char)requiredStart);
                    }
                    skipped = match < 0 ? length : match;
                }
                Work(skipped * rejectionCost);
                return skipped;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private char At(int offset)
            {
                if (typeof(TPattern) == typeof(byte))
                {
                    return (uint)offset < (uint)patternBytes.Length ? (char)patternBytes[offset] : '\0';
                }
                return (uint)offset < (uint)patternCharacters.Length ? patternCharacters[offset] : '\0';
            }

            private static bool Class(char value, char code)
            {
                int kind = code | 0x20;
                bool match;
                switch (kind)
                {
                    case 'a': match = IsAsciiLetter(value); break;
                    case 'c': match = value < 32 || value == 127; break;
                    case 'd': match = IsAsciiDigit(value); break;
                    case 'l': match = value >= 'a' && value <= 'z'; break;
                    case 'p': match = value >= 33 && value <= 126 && !IsAsciiLetter(value) && !IsAsciiDigit(value); break;
                    case 's': match = value == ' ' || value >= 9 && value <= 13; break;
                    case 'u': match = value >= 'A' && value <= 'Z'; break;
                    case 'w': match = IsAsciiLetter(value) || IsAsciiDigit(value); break;
                    case 'x': match = IsAsciiDigit(value) || (uint)((value | 0x20) - 'a') <= 'f' - 'a'; break;
                    case 'z': match = value == 0; break;
                    default: return value == code;
                }
                return code == kind ? match : !match;
            }

            private static bool IsAsciiLetter(char value) => (uint)((value | 0x20) - 'a') <= 'z' - 'a';

            private int BracketEnd(int start)
            {
                if (lastBracket.Start == start)
                {
                    Work(lastBracket.Cost);
                    return lastBracket.End;
                }
                int before = remaining;
                int cursor = start + (At(start + 1) == '^' ? 2 : 1);
                do
                {
                    Work();
                    char entry = At(cursor++);
                    if (entry == 0)
                    {
                        throw new RuntimeFault("malformed string pattern bracket");
                    }
                    if (entry == '%' && At(cursor) != 0)
                    {
                        cursor++;
                    }
                }
                while (At(cursor) != ']');
                int end = cursor + 1;
                lastBracket = (start, end, before - remaining);
                bracketCharacters.First = bracketCharacters.Second = -1;
                return end;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private bool AcceptAtom(char value, char atom, char escape)
            {
                Work();
                if (atom != '%') return atom == '.' || value == atom;
                return escape == 'd' ? IsAsciiDigit(value) : Class(value, escape);
            }

            private bool AcceptBracket(char value, int start, int end)
            {
                // BracketEnd invalidates these results on entry to a different set.
                if (value == bracketCharacters.First)
                {
                    Work(bracketCharacters.Cost);
                    return bracketCharacters.FirstMatch;
                }
                if (value == bracketCharacters.Second)
                {
                    Work(bracketCharacters.Cost);
                    return bracketCharacters.SecondMatch;
                }
                int before = remaining;
                Work();
                bool inverse = At(start + 1) == '^';
                bool match = false;
                for (int index = start + (inverse ? 2 : 1); index < end - 1; index++)
                {
                    Work();
                    char entry = At(index);
                    if (entry == '%')
                    {
                        index++;
                        match = match || Class(value, At(index));
                    }
                    else if (index + 2 < end - 1 && At(index + 1) == '-')
                    {
                        match = match || (value >= entry && value <= At(index + 2));
                        index += 2;
                    }
                    else
                    {
                        match = match || value == entry;
                    }
                }
                bool matched = inverse ? !match : match;
                bracketCharacters = (value, matched, bracketCharacters.First, bracketCharacters.FirstMatch, before - remaining);
                return matched;
            }

            private int RepeatBracket(int position, int start, int end, bool once, out int characterCost)
            {
                characterCost = 0;
                if (position == text.Length) return position;
                char first = text[position];
                int before = remaining;
                if (!AcceptBracket(first, start, end)) return position;
                characterCost = before - remaining;
                int limit = position + 1;
                if (once || limit == text.Length) return limit;
                if (text[limit] == first)
                {
                    int scanEnd = limit + Math.Min(text.Length - limit, remaining / characterCost);
                    while (limit < scanEnd && text[limit] == first) limit++;
                    Work((limit - position - 1) * characterCost);
                }
                if (limit == text.Length) return limit;
                char second = text[limit];
                if (!AcceptBracket(second, start, end)) return limit;
                limit++;
                return limit == text.Length ? limit : RepeatMixedBracket(limit, start, end, characterCost, first, second);
            }

            private int RepeatMixedBracket(int limit, int start, int end, int characterCost, char first, char second)
            {
                Span<ulong> acceptedBytes = stackalloc ulong[4];
                acceptedBytes.Clear();
                if (first <= byte.MaxValue) acceptedBytes[first >> 6] |= 1UL << (first & 63);
                if (second <= byte.MaxValue) acceptedBytes[second >> 6] |= 1UL << (second & 63);
                char previousWide = second > byte.MaxValue ? second : first;
                while (limit < text.Length)
                {
                    char value = text[limit];
                    if (value <= byte.MaxValue)
                    {
                        ref ulong word = ref acceptedBytes[value >> 6];
                        ulong bit = 1UL << (value & 63);
                        if ((word & bit) != 0)
                        {
                            Work(characterCost);
                        }
                        else
                        {
                            if (!AcceptBracket(value, start, end)) break;
                            word |= bit;
                        }
                    }
                    else if (value == previousWide)
                    {
                        Work(characterCost);
                    }
                    else
                    {
                        if (!AcceptBracket(value, start, end)) break;
                        previousWide = value;
                    }
                    limit++;
                }
                return limit;
            }

            private object Capture(int index)
            {
                var capture = CaptureRange(index);
                return capture.Length == -2 ? (object)(capture.Start + 1) : text.Substring(capture.Start, capture.Length);
            }

            internal (int Start, int Length) CaptureRange(int index)
            {
                var captures = buffers.Captures;
                if (index < 0 || index >= captures.Count)
                {
                    throw new RuntimeFault("invalid string pattern capture index");
                }
                var capture = captures[index];
                if (capture.Length == -1)
                {
                    throw new RuntimeFault("unfinished string pattern capture");
                }
                return capture;
            }

            internal object?[] Values(int start, int end, bool includePositions = false)
            {
                int count = buffers.Captures.Count;
                if (!includePositions && count == 0)
                {
                    return new object?[] { text.Substring(start, end - start) };
                }
                int offset = includePositions ? 2 : 0;
                var values = new object?[offset + count];
                if (includePositions)
                {
                    values[0] = start + 1;
                    values[1] = end;
                }
                for (int i = 0; i < count; i++)
                {
                    values[offset + i] = Capture(i);
                }
                return values;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static int LastOpenCapture(uint openCaptures)
            {
                int index = 0;
                if (openCaptures >= 1u << 16) { openCaptures >>= 16; index += 16; }
                if (openCaptures >= 1u << 8) { openCaptures >>= 8; index += 8; }
                if (openCaptures >= 1u << 4) { openCaptures >>= 4; index += 4; }
                if (openCaptures >= 1u << 2) { openCaptures >>= 2; index += 2; }
                return index + (int)(openCaptures >> 1);
            }

            internal bool Try(int start, int patternStart, out int end)
            {
                var captures = buffers.Captures;
                Stack<PatternChoice>? choices = buffers.Choices;
                captures.Clear();
                uint openCaptures = 0;
                int position = start;
                int instruction = patternStart;
                while (true)
                {
                    Work();
                    bool failed = false;
                    char op = At(instruction);
                    if (op == 0)
                    {
                        choices?.Clear();
                        end = position;
                        return true;
                    }
                    if (op == '(')
                    {
                        if (captures.Count == CaptureLimit)
                        {
                            throw new RuntimeFault("too many string pattern captures");
                        }
                        bool marker = At(instruction + 1) == ')';
                        if (!marker) openCaptures |= 1u << captures.Count;
                        captures.Add((position, marker ? -2 : -1));
                        instruction += marker ? 2 : 1;
                        continue;
                    }
                    if (op == ')')
                    {
                        if (openCaptures == 0)
                        {
                            throw new RuntimeFault("invalid string pattern capture");
                        }
                        int index = LastOpenCapture(openCaptures);
                        openCaptures &= ~(1u << index);
                        captures[index] = (captures[index].Start, position - captures[index].Start);
                        instruction++;
                        continue;
                    }
                    char escape = op == '%' ? At(instruction + 1) : '\0';
                    if (op == '$' && At(instruction + 1) == '\0')
                    {
                        failed = position != text.Length;
                        instruction++;
                    }
                    else if (escape == 'b')
                    {
                        char open = At(instruction + 2), close = At(instruction + 3);
                        if (open == 0 || close == 0)
                        {
                            throw new RuntimeFault("unbalanced string pattern");
                        }
                        int depth = 1;
                        if (position < text.Length && text[position] == open)
                        {
                            while (++position < text.Length)
                            {
                                Work();
                                if (text[position] == close)
                                {
                                    if (--depth == 0)
                                    {
                                        position++;
                                        break;
                                    }
                                }
                                else if (text[position] == open)
                                {
                                    depth++;
                                }
                            }
                        }
                        failed = depth != 0;
                        instruction += 4;
                    }
                    else if (escape == 'f')
                    {
                        int bracket = instruction + 2;
                        if (At(bracket) != '[')
                        {
                            throw new RuntimeFault("missing string pattern frontier bracket");
                        }
                        int after = BracketEnd(bracket);
                        failed = AcceptBracket(position == 0 ? '\0' : text[position - 1], bracket, after) ||
                            !AcceptBracket(position == text.Length ? '\0' : text[position], bracket, after);
                        instruction = after;
                    }
                    else if (IsAsciiDigit(escape))
                    {
                        int index = escape - '1';
                        var capture = CaptureRange(index);
                        failed = capture.Length < 0 || capture.Length > text.Length - position;
                        if (!failed)
                        {
                            Work(capture.Length);
                            failed = !text.RangeEquals(capture.Start, position, capture.Length);
                        }
                        if (!failed)
                        {
                            position += capture.Length;
                        }
                        instruction += 2;
                    }
                    else
                    {
                        if (op == '%' && escape == 0)
                        {
                            throw new RuntimeFault("malformed string pattern escape");
                        }
                        bool bracket = op == '[';
                        int after = bracket ? BracketEnd(instruction) : instruction + (op == '%' ? 2 : 1);
                        char repeat = At(after);
                        bool quantifier = repeat == '*' || repeat == '+' || repeat == '-' || repeat == '?';
                        if (!quantifier)
                        {
                            if (position < text.Length)
                            {
                                char value = text[position];
                                if (bracket ? AcceptBracket(value, instruction, after) : AcceptAtom(value, op, escape))
                                {
                                    position++;
                                    instruction = after;
                                    continue;
                                }
                            }
                            failed = true;
                        }
                        else
                        {
                            bool reuseRun = repeat != '?';
                            int limit = position;
                            if (reuseRun && repeatRun.Atom == instruction && position >= repeatRun.Start && position <= repeatRun.Limit)
                            {
                                limit = repeatRun.Limit;
                                // This slice cannot cost more than the original scan, which included the first rejection.
                                int checks = limit - position + (limit < text.Length ? 1 : 0);
                                Work(checks * repeatRun.CharacterCost);
                            }
                            else
                            {
                                int characterCost = 1;
                                if (bracket)
                                {
                                    limit = RepeatBracket(position, instruction, after, repeat == '?', out characterCost);
                                }
                                else
                                {
                                    while (limit < text.Length && AcceptAtom(text[limit], op, escape))
                                    {
                                        limit++;
                                        if (repeat == '?') break;
                                    }
                                }
                                if (reuseRun && limit - position > 1) repeatRun = (instruction, position, limit, characterCost);
                            }
                            int minimum = position + (repeat == '+' ? 1 : 0);
                            failed = limit < minimum;
                            if (!failed)
                            {
                                int step = repeat == '-' ? 1 : -1;
                                position = step == 1 ? minimum : limit;
                                instruction = after + 1;
                                if (limit > minimum)
                                {
                                    Stack<PatternChoice> pending = choices ??= (buffers.Choices = new Stack<PatternChoice>());
                                    if (pending.Count == 4096)
                                    {
                                        throw new RuntimeFault("string pattern choice limit");
                                    }
                                    pending.Push(new PatternChoice { Next = position + step, Last = step == 1 ? limit : minimum,
                                        Step = step, Pattern = instruction, CaptureCount = captures.Count,
                                        OpenCaptures = openCaptures });
                                }
                            }
                        }
                    }
                    if (!failed)
                    {
                        continue;
                    }
                    if (choices == null || choices.Count == 0)
                    {
                        end = start;
                        return false;
                    }
                    PatternChoice choice = choices.Pop();
                    position = choice.Next;
                    instruction = choice.Pattern;
                    // Restore captures that were open when this choice was recorded.
                    uint reopen = choice.OpenCaptures & ~openCaptures;
                    while (reopen != 0)
                    {
                        int index = LastOpenCapture(reopen);
                        reopen &= ~(1u << index);
                        captures[index] = (captures[index].Start, -1);
                    }
                    openCaptures = choice.OpenCaptures;
                    captures.RemoveRange(choice.CaptureCount, captures.Count - choice.CaptureCount);
                    if (choice.Next != choice.Last)
                    {
                        choice.Next += choice.Step;
                        choices.Push(choice);
                    }
                }
            }
        }
    }
}

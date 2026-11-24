using System;
using System.Buffers;
using System.Text;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private void RegisterStringPrimitives(ScriptTable library)
        {
            RegisterStringPatterns(library);
            static object?[] Case(object?[] args, bool upper)
            {
                RequireScriptArguments(args, 1);
                char firstLetter = upper ? 'a' : 'A';
                int delta = upper ? -32 : 32;
                static char ChangeAsciiCase(char character, char first, int offset) =>
                    (uint)(character - first) <= 25 ? (char)(character + offset) : character;

                if (args[0] is ScriptMutableString mutable)
                {
                    ReadOnlySpan<byte> bytes = mutable.Bytes;
                    int start = 0;
                    while (start < bytes.Length && (uint)(bytes[start] - firstLetter) > 25) start++;
                    if (start == bytes.Length) return new object?[] { ScriptChunk.Bytes.GetString(bytes) };

                    string result = string.Create(bytes.Length, (mutable, start, firstLetter, delta), static (characters, state) =>
                    {
                        ReadOnlySpan<byte> source = state.mutable.Bytes;
                        if (state.start != 0) ScriptChunk.Bytes.GetChars(source.Slice(0, state.start), characters);
                        for (int index = state.start; index < characters.Length; index++)
                        {
                            characters[index] = ChangeAsciiCase((char)source[index], state.firstLetter, state.delta);
                        }
                    });
                    return new object?[] { result };
                }

                string value = ScriptVm.Text(args[0]);
                for (int i = 0; i < value.Length; i++)
                {
                    if ((uint)(value[i] - firstLetter) > 25) continue;
                    string result = string.Create(value.Length, (value, start: i, firstLetter, delta), static (characters, state) =>
                    {
                        state.value.AsSpan(0, state.start).CopyTo(characters);
                        for (int index = state.start; index < characters.Length; index++)
                        {
                            characters[index] = ChangeAsciiCase(state.value[index], state.firstLetter, state.delta);
                        }
                    });
                    return new object?[] { result };
                }
                return new object?[] { value };
            }
            library["len"] = new ScriptVm.Native(args =>
            {
                RequireScriptArguments(args, 1);
                int length = args[0] is ScriptMutableString mutable ? mutable.Length : ScriptVm.Text(args[0]).Length;
                return new object?[] { length };
            });
            library["clen"] = new ScriptVm.Native(args => InspectTerminatedString(args, wide: false, extract: false));
            library["wlen"] = new ScriptVm.Native(args => InspectTerminatedString(args, wide: true, extract: false));
            library["cstr"] = new ScriptVm.Native(args => InspectTerminatedString(args, wide: false, extract: true));
            library["wstr"] = new ScriptVm.Native(args => InspectTerminatedString(args, wide: true, extract: true));
            library["lower"] = new ScriptVm.Native(args => Case(args, false));
            library["upper"] = new ScriptVm.Native(args => Case(args, true));
            library["format"] = new ScriptVm.Native(args => new object?[] { FormatScriptString(args) });
            library["new"] = new ScriptVm.Native(args =>
            {
                RequireScriptArguments(args, 1);
                int length = ScriptVm.Number(args[0]);
                return new object?[] { NewMutableScriptString(length) };
            });
            library["set"] = new ScriptVm.Native(args =>
            {
                RequireScriptArguments(args, 3);
                ScriptMutableString target = MutableString(args[0]);
                int index = MutableIndex(ScriptVm.Number(args[1]), target.Length);
                if (index < 0 || index >= target.Length)
                {
                    throw new RuntimeFault("string.set overflow");
                }
                Memory.Put8(target.Pointer + (uint)index, (byte)ScriptVm.Number(args[2]));
                return Array.Empty<object?>();
            });
            library["update"] = new ScriptVm.Native(args =>
            {
                RequireScriptArguments(args, 2);
                ScriptMutableString target = MutableString(args[0]);
                var mutable = args[1] as ScriptMutableString;
                string? sourceText = mutable == null ? ScriptVm.Text(args[1]) : null;
                int sourceLength = mutable?.Length ?? sourceText!.Length;
                int offset = MutableIndex(Number(args, 2, 1), target.Length);
                int start = MutableIndex(Number(args, 3, 1), sourceLength);
                int end = MutableIndex(Number(args, 4, sourceLength), sourceLength);
                long span = (long)end - start + 1;
                if (offset < 0 || offset > target.Length || start < 0 ||
                    end < -1 || span < 0)
                {
                    throw new RuntimeFault($"string.update range overflow: target={target.Length}, source={sourceLength}, " +
                        $"offset={offset}, start={start}, end={end}");
                }
                uint count = (uint)Math.Min(span, (long)target.Length - offset);
                if (count == 0)
                {
                    return Array.Empty<object?>();
                }
                uint destination = target.Pointer + (uint)offset;
                bool readsPastSource = (long)start + count > sourceLength;
                uint sourcePointer;
                if (mutable != null)
                {
                    if (!ReferenceEquals(mutable.Memory, Memory))
                    {
                        throw new RuntimeFault("string.update source belongs to another session");
                    }
                    sourcePointer = mutable.Pointer;
                }
                else if (readsPastSource)
                {
                    // mr_strlib.c str_update clamps only to the destination.
                    sourcePointer = ScriptStringPointer(sourceText!);
                }
                else
                {
                    CopyScriptUpdateText(destination, sourceText!, start, (int)count);
                    return Array.Empty<object?>();
                }
                if (readsPastSource)
                {
                    Record($"string.update native source range: length={sourceLength}, start={start}, count={count}");
                }
                Memory.Copy(destination, sourcePointer + (uint)start, count, preserveOverlap: true);
                return Array.Empty<object?>();
            });
            library["pupdate"] = new ScriptVm.Native(args =>
            {
                RequireScriptArguments(args, 4);
                uint target = unchecked((uint)ScriptVm.Number(args[0]));
                int targetLength = ScriptVm.Number(args[1]);
                uint source = unchecked((uint)ScriptVm.Number(args[2]));
                int sourceLength = ScriptVm.Number(args[3]);
                int Option(int index, int fallback)
                {
                    return args.Length > index && args[index] != null ? ScriptVm.Number(args[index]) : fallback;
                }
                int Index(int value, int length)
                {
                    return unchecked(value < 0 ? length + value : value - 1);
                }
                int offset = Index(Option(4, 1), targetLength);
                int start = Index(Option(5, 1), sourceLength);
                int end = Index(Option(6, sourceLength), sourceLength);
                long count = Math.Min((long)end - start + 1, (long)targetLength - offset);
                if (offset < 0 || targetLength < 0 || sourceLength < 0 || count < 0)
                {
                    throw new RuntimeFault("string.pupdate range overflow");
                }
                long targetStart = (long)target + offset;
                long sourceStart = (long)source + start;
                if (count != 0 && (targetStart > uint.MaxValue || sourceStart < 0 || sourceStart > uint.MaxValue))
                {
                    throw new RuntimeFault("string.pupdate address overflow");
                }
                Memory.Copy((uint)targetStart, (uint)sourceStart, (uint)count, true);
                return Array.Empty<object?>();
            });
            library["char"] = new ScriptVm.Native(args =>
            {
                string value = string.Create(args.Length, args, static (characters, values) =>
                {
                    for (int i = 0; i < characters.Length; i++)
                    {
                        int code = ScriptVm.Number(values[i]);
                        if ((uint)code > byte.MaxValue)
                        {
                            throw new RuntimeFault("string.char invalid byte value");
                        }
                        characters[i] = (char)code;
                    }
                });
                return new object?[] { value };
            });
            library["rep"] = new ScriptVm.Native(args =>
            {
                RequireScriptArguments(args, 1);
                var mutable = args[0] as ScriptMutableString;
                string value = mutable == null ? ScriptVm.Text(args[0]) : string.Empty;
                int seedLength = mutable?.Length ?? value.Length;
                RequireScriptArguments(args, 2);
                int count = ScriptVm.Number(args[1]);
                if (count <= 0 || seedLength == 0)
                {
                    return new object?[] { "" };
                }
                long length = (long)seedLength * count;
                if (length > Package.MaxBytes)
                {
                    throw new RuntimeFault("string.rep size limit");
                }
                if (count == 1) return new object?[] { mutable?.Text ?? value };
                if (seedLength == 1)
                {
                    char character = mutable == null ? value[0] : (char)mutable.Bytes[0];
                    return new object?[] { new string(character, count) };
                }
                string result = string.Create((int)length, (mutable, value, seedLength), static (characters, state) =>
                {
                    Span<char> seed = characters.Slice(0, state.seedLength);
                    if (state.mutable != null) ScriptChunk.Bytes.GetChars(state.mutable.Bytes, seed);
                    else state.value.AsSpan().CopyTo(seed);
                    int written = state.seedLength;
                    while (written < characters.Length)
                    {
                        int next = Math.Min(written, characters.Length - written);
                        characters.Slice(0, next).CopyTo(characters.Slice(written, next));
                        written += next;
                    }
                });
                return new object?[] { result };
            });
            library["u2c"] = new ScriptVm.Native(NarrowScriptWideString);
            library["c2u"] = new ScriptVm.Native(ConvertScriptTextToUnicode);
            Script.Globals["c2u"] = library["c2u"];
            library["dump"] = new ScriptVm.Native(args =>
            {
                RequireScriptArguments(args, 1);
                if (!ScriptVm.IsFunction(args[0]))
                {
                    throw new RuntimeFault("string.dump requires a function");
                }
                ScriptPrototype? prototype = ScriptVm.FunctionPrototype(args[args.Length - 1]);
                if (prototype == null || prototype.Upvalues != 0)
                {
                    throw new RuntimeFault("unable to dump given function");
                }
                return new object?[] { ScriptChunk.Bytes.GetString(ScriptChunk.Write(prototype)) };
            });
        }

        private static int TerminatedLength<T>(ReadOnlySpan<T> value, bool wide) where T : struct, IEquatable<T>
        {
            if (!wide)
            {
                int zero = value.IndexOf(default(T));
                return zero < 0 ? value.Length : zero;
            }
            for (int i = 0; i < value.Length; i += 2)
            {
                if (value[i].Equals(default) && (i + 1 == value.Length || value[i + 1].Equals(default)))
                {
                    return i;
                }
            }
            return value.Length;
        }

        private static object?[] InspectTerminatedString(object?[] args, bool wide, bool extract)
        {
            RequireScriptArguments(args, 1);
            if (args[0] is ScriptMutableString mutable)
            {
                ReadOnlySpan<byte> bytes = mutable.Bytes;
                int length = TerminatedLength(bytes, wide);
                return new object?[] { extract ? ScriptChunk.Bytes.GetString(bytes.Slice(0, length)) : length };
            }
            string text = ScriptVm.Text(args[0]);
            int prefix = TerminatedLength(text.AsSpan(), wide);
            return new object?[] { extract ? text.Substring(0, prefix) : prefix };
        }

        private static int NarrowPrefixLength<T>(ReadOnlySpan<T> value) where T : struct, IEquatable<T>
        {
            int count = 0;
            for (int i = 0; i + 1 < value.Length && value[i].Equals(default) && !value[i + 1].Equals(default); i += 2)
            {
                count++;
            }
            return count;
        }

        private static object?[] NarrowScriptWideString(object?[] args)
        {
            RequireScriptArguments(args, 1);
            string result;
            if (args[0] is ScriptMutableString mutable)
            {
                int count = NarrowPrefixLength(mutable.Bytes);
                result = string.Create(count, mutable, static (destination, source) =>
                {
                    ReadOnlySpan<byte> bytes = source.Bytes;
                    for (int i = 0; i < destination.Length; i++) destination[i] = (char)bytes[i * 2 + 1];
                });
            }
            else
            {
                string text = ScriptVm.Text(args[0]);
                int count = NarrowPrefixLength(text.AsSpan());
                result = string.Create(count, text, static (destination, source) =>
                {
                    for (int i = 0; i < destination.Length; i++) destination[i] = source[i * 2 + 1];
                });
            }
            return new object?[] { result };
        }

        private object?[] ConvertScriptTextToUnicode(object?[] args)
        {
            RequireScriptArguments(args, 1);
            const string encodingError = "c2u requires an explicit CP936 encoding service";
            const int poolLimit = 64 * 1024;
            ReadOnlySpan<char> text = default;
            ReadOnlySpan<byte> bytes = default;
            Encoding? decoder = null;
            int inputLength;
            if (args[0] is ScriptMutableString mutable)
            {
                bytes = mutable.Bytes;
                inputLength = TerminatedLength(bytes, wide: false);
            }
            else
            {
                text = ScriptVm.Text(args[0]).AsSpan();
                text = text.Slice(0, TerminatedLength(text, wide: false));
                decoder = RequireGbkEncoding(encodingError);
                inputLength = ScriptChunk.Bytes.GetByteCount(text);
            }
            byte[]? inputRental = null;
            byte[]? outputRental = null;
            try
            {
                Span<byte> input = inputLength <= 512 ? stackalloc byte[inputLength] :
                    inputLength <= poolLimit ? (inputRental = ArrayPool<byte>.Shared.Rent(inputLength)).AsSpan(0, inputLength) :
                    new byte[inputLength];
                if (decoder == null)
                {
                    // The host encoding can reenter the VM when cloned.
                    bytes.Slice(0, inputLength).CopyTo(input);
                    decoder = RequireGbkEncoding(encodingError);
                }
                else
                {
                    ScriptChunk.Bytes.GetBytes(text, input);
                }
                int outputLength = ConvertedTextSize(input, out _);
                Span<byte> output = outputLength <= 512 ? stackalloc byte[outputLength] :
                    outputLength <= poolLimit ? (outputRental = ArrayPool<byte>.Shared.Rent(outputLength)).AsSpan(0, outputLength) :
                    new byte[outputLength];
                ConvertTextBytes(input, output, decoder);
                return new object?[] { ScriptChunk.Bytes.GetString(output) };
            }
            finally
            {
                if (outputRental != null) ArrayPool<byte>.Shared.Return(outputRental, clearArray: true);
                if (inputRental != null) ArrayPool<byte>.Shared.Return(inputRental, clearArray: true);
            }
        }

        private void CopyScriptUpdateText(uint destination, string value, int start, int count)
        {
            int length = ScriptChunk.Bytes.GetByteCount(value);
            byte[]? rented = null;
            Span<byte> bytes = length <= 512 ? stackalloc byte[length] :
                length <= 64 * 1024 ? (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length) : new byte[length];
            try
            {
                ScriptChunk.Bytes.GetBytes(value.AsSpan(), bytes);
                if (start + count <= bytes.Length)
                {
                    Memory.Write(destination, bytes.Slice(start, count));
                    return;
                }

                // Host-supplied strings can encode to fewer bytes than their script indices describe.
                uint source = Memory.Allocate((uint)bytes.Length);
                try
                {
                    Memory.Write(source, bytes);
                    Memory.Copy(destination, source + (uint)start, (uint)count, preserveOverlap: true);
                }
                finally { Memory.Free(source); }
            }
            finally
            {
                if (rented != null) ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }

        private ScriptMutableString MutableString(object? value)
        {
            if (value is ScriptMutableString mutable && ReferenceEquals(mutable.Memory, Memory))
            {
                if (mutable.InternedText != null && mutable.TextEquals(mutable.InternedText) &&
                    ReferenceEquals(CanonicalScriptString(mutable.InternedText), mutable))
                {
                    Script.ReplaceInternedString(mutable.InternedText, mutable, StringAliasRoots(), ScriptGcChildren);
                }
                return mutable;
            }
            if (value is string || value is int)
            {
                return InternedScriptString(ScriptVm.Text(value));
            }
            throw new RuntimeFault("mutable string storage required");
        }

        private static int MutableIndex(int index, int length)
        {
            return index < 0 ? checked(length + index) : index - 1;
        }

        private static int FormattedStringLength<T>(ReadOnlySpan<T> text, int precision) where T : struct, IEquatable<T>
        {
            // A long string without precision preserves any embedded terminator.
            if (precision < 0 && text.Length >= 100) return text.Length;
            if (precision >= 0 && precision < text.Length) text = text.Slice(0, precision);
            return TerminatedLength(text, wide: false);
        }

        private static void AppendStringBytes(StringBuilder result, ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length <= 16)
            {
                foreach (byte value in bytes) result.Append((char)value);
                return;
            }
            Span<char> characters = stackalloc char[256];
            while (!bytes.IsEmpty)
            {
                int length = Math.Min(bytes.Length, characters.Length);
                Span<char> chunk = characters.Slice(0, length);
                ScriptChunk.Bytes.GetChars(bytes.Slice(0, length), chunk);
                result.Append(chunk);
                bytes = bytes.Slice(length);
            }
        }

        private static string FormatSingleStringArgument(object? value)
        {
            var mutable = value as ScriptMutableString;
            ReadOnlySpan<byte> bytes = mutable == null ? default : mutable.Bytes;
            string? text = mutable == null ? ScriptVm.Text(value) : null;
            int length = text == null
                ? FormattedStringLength(bytes, precision: -1)
                : FormattedStringLength(text.AsSpan(), precision: -1);
            if (length > Package.MaxBytes)
            {
                throw new RuntimeFault("string.format size limit");
            }
            return text == null ? ScriptChunk.Bytes.GetString(bytes.Slice(0, length)) : text.Substring(0, length);
        }

        private string FormatScriptString(object?[] args)
        {
            static int NextMarker(string text, int start)
            {
                if (start == text.Length) return -1;
                if (text[start] == '%') return start;
                return start + 1 == text.Length ? -1 : text.IndexOf('%', start + 1);
            }

            RequireScriptArguments(args, 1);
            string format = ScriptVm.Text(args[0]);
            if (format.Length > Package.MaxBytes)
            {
                throw new RuntimeFault("string.format size limit");
            }
            int marker = NextMarker(format, 0);
            if (marker < 0) return format;
            if (format.Length == 2 && format[1] == 's')
            {
                RequireScriptArguments(args, 2);
                return FormatSingleStringArgument(args[1]);
            }

            var result = new StringBuilder();
            int argument = 1;
            int cursor = 0;
            void CheckAdditionalLength(int length)
            {
                if ((long)result.Length + length > Package.MaxBytes)
                {
                    throw new RuntimeFault("string.format size limit");
                }
            }
            void Append(ReadOnlySpan<char> text)
            {
                CheckAdditionalLength(text.Length);
                result.Append(text);
            }
            void AppendQuotedBytes(ReadOnlySpan<byte> bytes)
            {
                ReadOnlySpan<byte> escapes = stackalloc byte[] { 0, (byte)'"', (byte)'\\', (byte)'\n' };
                CheckAdditionalLength(bytes.Length);
                while (!bytes.IsEmpty)
                {
                    int mark = bytes.IndexOfAny(escapes);
                    if (mark != 0)
                    {
                        int length = mark < 0 ? bytes.Length : mark;
                        CheckAdditionalLength(length);
                        AppendStringBytes(result, bytes.Slice(0, length));
                    }
                    if (mark < 0) return;
                    char character = (char)bytes[mark];
                    if (character == 0) Append("\\000");
                    else
                    {
                        CheckAdditionalLength(2);
                        result.Append('\\').Append(character);
                    }
                    bytes = bytes.Slice(mark + 1);
                }
            }
            int Field()
            {
                int start = cursor;
                int value = 0;
                while (cursor < format.Length && format[cursor] >= '0' && format[cursor] <= '9')
                {
                    if (cursor - start == 2)
                    {
                        throw new RuntimeFault("string.format width or precision too long");
                    }
                    value = value * 10 + format[cursor++] - '0';
                }
                return value;
            }
            do
            {
                if (marker > cursor) Append(format.AsSpan(cursor, marker - cursor));
                int start = marker;
                cursor = marker + 1;
                if (cursor < format.Length && format[cursor] == '%')
                {
                    Append("%");
                    cursor++;
                    continue;
                }
                PrintfFlags flags = ReadPrintfFlags(format, ref cursor);
                bool left = (flags & PrintfFlags.Left) != 0;
                int width = Field();
                int precision = -1;
                if (cursor < format.Length && format[cursor] == '.')
                {
                    cursor++;
                    precision = Field();
                }
                if (cursor - start + 1 > 20)
                {
                    throw new RuntimeFault("string.format format too long");
                }
                if (cursor == format.Length || "cdiouxXqs".IndexOf(format[cursor]) < 0)
                {
                    throw new RuntimeFault("string.format invalid option");
                }
                char conversion = format[cursor++];
                RequireScriptArguments(args, argument + 1);
                object? value = args[argument++];
                if (conversion == 'q')
                {
                    Append("\"");
                    if (value is ScriptMutableString mutable)
                    {
                        AppendQuotedBytes(mutable.Bytes);
                    }
                    else
                    {
                        foreach (char character in ScriptVm.Text(value))
                        {
                            if (character == 0)
                            {
                                Append("\\000");
                            }
                            else
                            {
                                bool escape = character == '"' || character == '\\' || character == '\n';
                                CheckAdditionalLength(escape ? 2 : 1);
                                if (escape) result.Append('\\');
                                result.Append(character);
                            }
                        }
                    }
                    Append("\"");
                }
                else if (conversion == 's')
                {
                    var mutable = value as ScriptMutableString;
                    ReadOnlySpan<byte> bytes = mutable == null ? default : mutable.Bytes;
                    ReadOnlySpan<char> text = mutable == null ? ScriptVm.Text(value).AsSpan() : default;
                    int length = mutable == null ? FormattedStringLength(text, precision) : FormattedStringLength(bytes, precision);
                    int padding = Math.Max(0, width - length);
                    CheckAdditionalLength(length + padding);
                    if (!left) result.Append(' ', padding);
                    if (mutable == null) result.Append(text.Slice(0, length));
                    else AppendStringBytes(result, bytes.Slice(0, length));
                    if (left) result.Append(' ', padding);
                }
                else
                {
                    int number = ScriptVm.Number(value);
                    if (conversion == 'c')
                    {
                        char character = (char)(number & 255);
                        int padding = Math.Max(0, width - 1);
                        CheckAdditionalLength(character == '\0' ? (left ? 0 : padding) : padding + 1);
                        if (!left) result.Append(' ', padding);
                        if (character != '\0')
                        {
                            result.Append(character);
                            if (left) result.Append(' ', padding);
                        }
                    }
                    else
                    {
                        AppendPrintfInteger(result, number, unchecked((uint)number), conversion, width, precision, flags);
                        CheckAdditionalLength(0);
                    }
                }
            }
            while ((marker = NextMarker(format, cursor)) >= 0);
            if (cursor < format.Length) Append(format.AsSpan(cursor));
            return result.ToString();
        }
    }
}

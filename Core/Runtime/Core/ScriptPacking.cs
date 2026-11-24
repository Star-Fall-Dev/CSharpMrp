using System;
using System.Collections.Generic;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private static IEnumerable<(char Kind, int Size, int Alignment, bool BigEndian)> PackingFields(string format)
        {
            int end = format.IndexOf('\0');
            end = end < 0 ? format.Length : end;
            if (end > 100000)
            {
                throw new RuntimeFault("string packing format limit");
            }
            int position = 0;
            bool bigEndian = position < end && format[position] == '>';
            if (position < end && (format[position] == '<' || format[position] == '>'))
            {
                position++;
            }
            int Number(int fallback)
            {
                if (position >= end || format[position] < '0' || format[position] > '9')
                {
                    return fallback;
                }
                long value = 0;
                while (position < end && format[position] >= '0' && format[position] <= '9')
                {
                    value = value * 10 + format[position++] - '0';
                    if (value > Package.MaxBytes)
                    {
                        throw new RuntimeFault("string packing size limit");
                    }
                }
                return (int)value;
            }
            int alignment = 1;
            if (position < end && format[position] == '@')
            {
                position++;
                alignment = Number(4);
            }
            while (position < end)
            {
                char kind = format[position++];
                int size = kind switch
                {
                    'h' or 'H' => 2,
                    'i' or 'I' => 4,
                    'l' or 'L' => Math.Min(4, Number(4)),
                    'c' => Number(1),
                    's' or 'p' => 0,
                    _ => 1
                };
                int boundary = Math.Min(alignment, kind is 'c' or 's' or 'p' ? 1 : size);
                if (boundary == 0 || (boundary & (boundary - 1)) != 0)
                {
                    throw new RuntimeFault("string packing alignment must be 2^n");
                }
                yield return (kind, size, boundary, bigEndian);
            }
        }

        private static int PackingExtent(long position)
        {
            if (position < 0 || position > Package.MaxBytes)
            {
                throw new RuntimeFault("string packing size limit");
            }
            return (int)position;
        }

        private static object?[] ScriptPack(object?[] args)
        {
            RequireScriptArguments(args, 1);
            using var output = new ScriptStore.EncodedBuffer(transferOwnership: false, maximumSize: Package.MaxBytes);
            int position = 0;
            int argument = 1;
            foreach (var field in PackingFields(ScriptVm.Text(args[0])))
            {
                int padding = (-position) & (field.Alignment - 1);
                position = PackingExtent((long)position + padding);
                if (padding != 0) output.Reserve(padding).Clear();
                int size = field.Size;
                if (field.Kind is 'b' or 'B' or 'h' or 'H' or 'l' or 'L' or 'i' or 'I')
                {
                    RequireScriptArguments(args, argument + 1);
                    uint value = unchecked((uint)ScriptVm.Number(args[argument]));
                    Span<byte> target = output.Reserve(size);
                    for (int i = 0; i < size; i++)
                    {
                        int shift = (field.BigEndian ? size - i - 1 : i) * 8;
                        target[i] = (byte)(value >> shift);
                    }
                }
                else if (field.Kind is 'c' or 's' or 'p')
                {
                    RequireScriptArguments(args, argument + 1);
                    var mutable = args[argument] as ScriptMutableString;
                    ReadOnlySpan<byte> bytes = mutable == null ? default : mutable.Bytes;
                    string value = mutable == null ? ScriptVm.Text(args[argument]) : string.Empty;
                    int valueLength = mutable == null ? value.Length : bytes.Length;
                    size = size == 0 ? valueLength : size;
                    int terminator = field.Kind == 's' ? 1 : 0;
                    PackingExtent((long)position + size + terminator);

                    int copied = Math.Min(size, valueLength);
                    Span<byte> target = output.Reserve(size + terminator);
                    if (mutable == null)
                    {
                        for (int i = 0; i < copied; i++) target[i] = (byte)value[i];
                    }
                    else
                    {
                        bytes.Slice(0, copied).CopyTo(target);
                    }
                    target.Slice(copied).Clear();
                    size += terminator;
                }
                else if (field.Kind == 'x')
                {
                    output.Reserve(1)[0] = 0;
                    argument--;
                }
                else if (field.Kind != ' ')
                {
                    throw new RuntimeFault("invalid string packing format " + field.Kind);
                }
                position = PackingExtent((long)position + size);
                argument++;
            }
            return new object?[] { ScriptChunk.Bytes.GetString(output.Bytes.AsSpan(0, output.Length)) };
        }

        private static object?[] ScriptUnpack(object?[] args)
        {
            RequireScriptArguments(args, 2);
            string format = ScriptVm.Text(args[0]);
            var mutable = args[1] as ScriptMutableString;
            ReadOnlySpan<byte> bytes = mutable == null ? default : mutable.Bytes;
            string? input = mutable == null ? ScriptVm.Text(args[1]) : null;
            int inputLength = input?.Length ?? bytes.Length;
            long position = args.Length < 3 || args[2] == null ? 0 : (long)ScriptVm.Number(args[2]) - 1;
            if (position < 0)
            {
                throw new RuntimeFault("string unpack invalid position");
            }
            var results = new List<object?>();
            foreach (var field in PackingFields(format))
            {
                position = (position + field.Alignment - 1) & -(long)field.Alignment;
                int size = field.Size;
                if (position + size > inputLength)
                {
                    throw new RuntimeFault("string unpack truncated input");
                }
                if (field.Kind is 'b' or 'B' or 'h' or 'H' or 'l' or 'L' or 'i' or 'I')
                {
                    uint value = 0;
                    for (int i = 0; i < size; i++)
                    {
                        int offset = (int)position + (field.BigEndian ? i : size - i - 1);
                        value = value << 8 | (input == null ? bytes[offset] : (byte)input[offset]);
                    }
                    if (field.Kind >= 'a' && field.Kind <= 'z' && size < 4 && (value & (1u << (size * 8 - 1))) != 0)
                    {
                        value |= uint.MaxValue << (size * 8);
                    }
                    results.Add(unchecked((int)value));
                }
                else if (field.Kind is 'c' or 's' or 'p')
                {
                    if (field.Kind == 'p')
                    {
                        object? previous = results.Count == 0 ? (object?)input ?? mutable : results[results.Count - 1];
                        if (!ScriptInteger(previous, out size) || size < 0)
                        {
                            throw new RuntimeFault("string unpack previous size for p missing or negative");
                        }
                    }
                    else if (field.Kind == 's')
                    {
                        int offset = (int)position;
                        size = input == null ? bytes.Slice(offset).IndexOf((byte)0) : input.AsSpan(offset).IndexOf('\0');
                        if (size < 0)
                        {
                            throw new RuntimeFault("string unpack unfinished string");
                        }
                    }
                    if (position + size > inputLength)
                    {
                        throw new RuntimeFault("string unpack truncated input");
                    }
                    results.Add(input == null
                        ? ScriptChunk.Bytes.GetString(bytes.Slice((int)position, size))
                        : input.Substring((int)position, size));
                    size += field.Kind == 's' ? 1 : 0;
                }
                else if (field.Kind != 'x' && field.Kind != ' ')
                {
                    throw new RuntimeFault("invalid string packing format " + field.Kind);
                }
                position += size;
                if (results.Count >= ScriptVm.StackLimit)
                {
                    throw new RuntimeFault("string unpack result limit");
                }
            }
            results.Add(unchecked((int)(position + 1)));
            return results.ToArray();
        }

        private static object?[] ScriptPackLength(object?[] args)
        {
            RequireScriptArguments(args, 1);
            int size = 0;
            foreach (var field in PackingFields(ScriptVm.Text(args[0])))
            {
                if (field.Size == 0)
                {
                    throw new RuntimeFault("string packLen has variable or zero size");
                }
                size = PackingExtent(((long)size + field.Alignment - 1 & -(long)field.Alignment) + field.Size);
            }
            return new object?[] { size };
        }
    }
}

using System;
using System.Globalization;
using System.Text;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private void RegisterTableGlobals()
        {
            ScriptTable Table(object?[] args)
            {
                return args.Length > 0 && args[0] is ScriptTable value ? value
                    : throw new RuntimeFault("table operation requires a table");
            }
            int Length(ScriptTable value)
            {
                return value.SequenceLength();
            }
            var library = new ScriptTable();
            library["sort"] = new ScriptVm.Native(Script.SortTable);
            library["next"] = new ScriptVm.Native(ScriptVm.IterateTable);
            library["iPairs"] = new ScriptVm.Native(ScriptIPairs);
            library["pairs"] = new ScriptVm.Native(args =>
                new object?[] { Script.Globals["_next"], Table(args), null });
            library["rawGet"] = new ScriptVm.Native(args =>
            {
                ScriptTable value = Table(args);
                RequireScriptArguments(args, 2);
                return new object?[] { value[args[1]] };
            });
            library["rawSet"] = new ScriptVm.Native(args =>
            {
                ScriptTable value = Table(args);
                RequireScriptArguments(args, 3);
                value[args[args.Length - 2]] = args[args.Length - 1];
                return new object?[] { args[args.Length - 3] };
            });
            library["items"] = new ScriptVm.Native(args =>
            {
                ScriptTable value = Table(args);
                int length = Length(value);
                if (length > ScriptVm.StackLimit)
                {
                    throw new RuntimeFault("table items result limit");
                }
                var results = new object?[length];
                for (int index = 0; index < length; index++)
                {
                    results[index] = value[index + 1];
                }
                return results;
            });
            library["concat"] = new ScriptVm.Native(args =>
            {
                string separator = args.Length < 2 || args[1] == null ? "" : ScriptVm.Text(args[1]);
                int first = args.Length < 3 || args[2] == null ? 1 : ScriptVm.Number(args[2]);
                int last = args.Length < 4 || args[3] == null ? 0 : ScriptVm.Number(args[3]);
                ScriptTable value = Table(args);
                if (last == 0)
                {
                    last = Length(value);
                }
                ScriptVm.CheckTableWork((long)last - first + 1);
                var result = new StringBuilder();
                Span<char> digits = stackalloc char[11]; // Sign plus ten decimal digits.
                for (long index = first; index <= last; index++)
                {
                    ReadOnlySpan<char> characters = digits.Slice(0, 0);
                    ReadOnlySpan<byte> bytes = default;
                    switch (value[(int)index])
                    {
                        case string text:
                            characters = text.AsSpan();
                            break;
                        case ScriptMutableString mutable:
                            bytes = mutable.Bytes;
                            break;
                        case int number:
                            number.TryFormat(digits, out int written, provider: CultureInfo.InvariantCulture);
                            characters = digits.Slice(0, written);
                            break;
                        default:
                            throw new RuntimeFault("table contains non-strings");
                    }
                    int separatorLength = index == last ? 0 : separator.Length;
                    if ((long)result.Length + characters.Length + bytes.Length + separatorLength > Package.MaxBytes)
                    {
                        throw new RuntimeFault("table concat size limit");
                    }
                    if (bytes.IsEmpty) result.Append(characters);
                    else AppendStringBytes(result, bytes);
                    if (index != last)
                    {
                        result.Append(separator);
                    }
                }
                return new object?[] { result.ToString() };
            });
            object?[] Visit(object?[] args, bool indexed) => Script.VisitTable(Table(args), args, indexed);
            library["forEach"] = new ScriptVm.Native(args => Visit(args, false));
            library["foreach"] = new ScriptVm.Native(args => Visit(args, false));
            library["iforEach"] = new ScriptVm.Native(args => Visit(args, true));
            library["foreachi"] = new ScriptVm.Native(args => Visit(args, true));
            library["getn"] = new ScriptVm.Native(args => new object?[] { Length(Table(args)) });
            library["getArrSize"] = new ScriptVm.Native(args => new object?[] { Length(Table(args)) });
            object?[] SetLength(object?[] args)
            {
                ScriptTable value = Table(args);
                RequireScriptArguments(args, 2);
                value.SetSequenceLength(ScriptVm.Number(args[1]));
                return Array.Empty<object?>();
            }
            library["setn"] = new ScriptVm.Native(SetLength);
            library["setArrSize"] = new ScriptVm.Native(SetLength);
            library["insert"] = new ScriptVm.Native(args =>
            {
                ScriptTable value = Table(args);
                if (args.Length < 2)
                {
                    throw new RuntimeFault("table.insert requires a value");
                }
                int length = Length(value);
                if (length == int.MaxValue)
                {
                    throw new RuntimeFault("table insertion size overflow");
                }
                int position = args.Length == 2 ? length + 1 : ScriptVm.Number(args[1]);
                int nextLength = Math.Max(length + 1, position);
                ScriptVm.CheckTableWork((long)length - position + 1);
                value.SetSequenceLength(nextLength);
                for (int index = length; index >= position; index--)
                {
                    value[index + 1] = value[index];
                }
                value[position] = args[args.Length == 2 ? 1 : 2];
                return Array.Empty<object?>();
            });
            library["remove"] = new ScriptVm.Native(args =>
            {
                ScriptTable value = Table(args);
                int length = Length(value);
                int position = args.Length < 2 || args[1] == null ? length : ScriptVm.Number(args[1]);
                if (length == 0)
                {
                    return Array.Empty<object?>();
                }
                ScriptVm.CheckTableWork((long)length - position);
                value.SetSequenceLength(length - 1);
                object? removed = value[position];
                for (int index = position; index < length; index++)
                {
                    value[index] = value[index + 1];
                }
                value[length] = null;
                return new[] { removed };
            });
            Script.Globals["table"] = library;
        }
    }

    public sealed partial class ScriptVm
    {
        internal static void CheckTableWork(long count)
        {
            if (count > 100000)
            {
                throw new RuntimeFault("table operation work limit");
            }
        }

        internal object?[] VisitTable(ScriptTable table, object?[] args, bool indexed)
        {
            int length = indexed ? table.SequenceLength() : 0;
            if (args.Length < 2 || !IsFunction(args[1]))
            {
                throw new RuntimeFault("table traversal requires a function");
            }
            // The current key and value remain on the traversal stack during the callback.
            var cursor = indexed ? null : new object?[2];
            if (cursor != null) activeNativeArguments.Add(cursor);
            object?[]? reusableArguments = null;
            try
            {
                int visited = 0;
                while (true)
                {
                    object? key;
                    object? item;
                    if (cursor != null)
                    {
                        if (!table.TryNext(cursor[0], out key, out item))
                        {
                            return Array.Empty<object?>();
                        }
                        cursor[0] = key;
                        cursor[1] = item;
                    }
                    else
                    {
                        if (visited == length)
                        {
                            return Array.Empty<object?>();
                        }
                        key = visited + 1;
                        item = table[key];
                    }
                    CheckTableWork(++visited);
                    object? callback = args[1];
                    object?[] arguments = callback is Closure ? (reusableArguments ??= new object?[2]) : new object?[2];
                    arguments[0] = key;
                    arguments[1] = item;
                    object?[] results = Call(callback, arguments);
                    if (results.Length != 0 && results[0] != null)
                    {
                        return new object?[] { results[0] };
                    }
                }
            }
            finally
            {
                if (cursor != null) activeNativeArguments.RemoveAt(activeNativeArguments.Count - 1);
            }
        }

        internal object?[] SortTable(object?[] args)
        {
            if (args.Length == 0 || !(args[0] is ScriptTable table))
            {
                throw new RuntimeFault("table operation requires a table");
            }
            int length = table.SequenceLength();
            object? comparator = args.Length > 1 ? args[1] : null;
            if (comparator != null && !IsFunction(comparator))
            {
                throw new RuntimeFault("table sort requires a function");
            }
            CheckTableWork(length);
            if (length > 1)
            {
                // Comparators can remove table entries and run GC before the next swap.
                var operands = new object?[3];
                activeNativeArguments.Add(operands);
                try
                {
                    SortTableEntries(table, length, comparator, operands);
                }
                finally
                {
                    activeNativeArguments.RemoveAt(activeNativeArguments.Count - 1);
                }
            }
            return Array.Empty<object?>();
        }

        private void SortTableEntries(ScriptTable table, int length, object? comparator, object?[] operands)
        {
            const int pivotSlot = 0, leftSlot = 1, rightSlot = 2;
            // Script closures copy arguments into their frame; native callbacks can retain the array.
            var reusableArguments = comparator is Closure ? new object?[2] : null;
            int comparisons = 0;
            bool Less(object? left, object? right)
            {
                if (++comparisons > 4000000)
                {
                    throw new RuntimeFault("table sort comparison limit");
                }
                if (comparator == null)
                {
                    return CompareValue(left, right, false);
                }
                object?[] arguments = reusableArguments ?? new object?[2];
                arguments[0] = left;
                arguments[1] = right;
                object?[] result = Call(comparator, arguments);
                return result.Length != 0 && Truth(result[0]);
            }
            void OrderEnds(object left, object right)
            {
                operands[leftSlot] = table[left];
                operands[rightSlot] = table[right];
                if (Less(operands[rightSlot], operands[leftSlot]))
                {
                    table[left] = operands[rightSlot];
                    table[right] = operands[leftSlot];
                }
                operands[leftSlot] = operands[rightSlot] = null;
            }
            // Visiting the smaller partition first halves the range for each deeper pending level.
            Span<(int First, int Last)> pending = stackalloc (int, int)[32];
            int pendingCount = 1;
            pending[0] = (1, length);
            while (pendingCount != 0)
            {
                var range = pending[--pendingCount];
                int first = range.First;
                int last = range.Last;
                if (last <= first)
                {
                    continue;
                }
                object firstKey = first;
                object lastKey = last;
                OrderEnds(firstKey, lastKey);
                if (last - first == 1)
                {
                    continue;
                }
                object middleKey = first + (last - first) / 2;
                operands[leftSlot] = table[middleKey];
                operands[rightSlot] = table[firstKey];
                if (Less(operands[leftSlot], operands[rightSlot]))
                {
                    table[middleKey] = operands[rightSlot];
                    table[firstKey] = operands[leftSlot];
                }
                else
                {
                    operands[rightSlot] = table[lastKey];
                    if (Less(operands[rightSlot], operands[leftSlot]))
                    {
                        table[middleKey] = operands[rightSlot];
                        table[lastKey] = operands[leftSlot];
                    }
                }
                operands[leftSlot] = operands[rightSlot] = null;
                if (last - first == 2)
                {
                    continue;
                }
                object pivotKey = last - 1;
                operands[pivotSlot] = table[middleKey];
                table[middleKey] = table[pivotKey];
                table[pivotKey] = operands[pivotSlot];
                int forward = first;
                int backward = last - 1;
                object forwardKey;
                while (true)
                {
                    do
                    {
                        forwardKey = ++forward;
                        operands[leftSlot] = table[forwardKey];
                        if (!Less(operands[leftSlot], operands[pivotSlot]))
                        {
                            break;
                        }
                        if (forward > last)
                        {
                            throw new RuntimeFault("invalid order function for sorting");
                        }
                    } while (true);
                    object backwardKey;
                    do
                    {
                        backwardKey = --backward;
                        operands[rightSlot] = table[backwardKey];
                        if (!Less(operands[pivotSlot], operands[rightSlot]))
                        {
                            break;
                        }
                        if (backward < first)
                        {
                            throw new RuntimeFault("invalid order function for sorting");
                        }
                    } while (true);
                    if (backward < forward)
                    {
                        break;
                    }
                    table[forwardKey] = operands[rightSlot];
                    table[backwardKey] = operands[leftSlot];
                    operands[leftSlot] = operands[rightSlot] = null;
                }
                operands[pivotSlot] = operands[leftSlot] = operands[rightSlot] = null;
                object? parked = table[pivotKey];
                table[pivotKey] = table[forwardKey];
                table[forwardKey] = parked;
                var leftRange = (first, forward - 1);
                var rightRange = (forward + 1, last);
                bool leftSmaller = forward - first < last - forward;
                pending[pendingCount++] = leftSmaller ? rightRange : leftRange;
                pending[pendingCount++] = leftSmaller ? leftRange : rightRange;
            }
        }
    }
}

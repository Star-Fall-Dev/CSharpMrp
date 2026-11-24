using System;
using System.Collections.Generic;

namespace CSharpMrp
{
    public sealed partial class ScriptVm
    {
        internal static ScriptTable? Metatable(object? value)
        {
            return value is ScriptTable table ? table.Metatable : value is ScriptUserdata userdata ? userdata.Metatable : null;
        }

        internal static bool SetMetatable(object? value, ScriptTable? metatable)
        {
            if (value is ScriptTable table)
            {
                table.Metatable = metatable;
                return true;
            }
            if (value is ScriptUserdata userdata)
            {
                userdata.Metatable = metatable;
                return true;
            }
            return false;
        }

        private object? FirstResult(object? function, params object?[] arguments)
        {
            object?[] result = Call(function, arguments);
            return result.Length == 0 ? null : result[0];
        }

        internal object? GetValue(object? target, object? key)
        {
            for (int depth = 0; depth <= 100; depth++)
            {
                object? method = Metatable(target)?["__index"];
                if (target is ScriptTable table)
                {
                    object? value = table[key];
                    if (value != null || method == null)
                    {
                        return CanonicalString(value);
                    }
                }
                else if (method == null)
                {
                    throw new RuntimeFault("script: expected table or __index method");
                }
                if (IsFunction(method))
                {
                    return FirstResult(method, target, key);
                }
                target = method;
            }
            throw new RuntimeFault("script: loop in __index");
        }

        internal void SetValue(object? target, object? key, object? value)
        {
            if (key == null)
            {
                throw new RuntimeFault("script table: nil key");
            }
            for (int depth = 0; depth <= 100; depth++)
            {
                object? method = Metatable(target)?["__newindex"];
                if (target is ScriptTable table)
                {
                    if (table[key] != null || method == null)
                    {
                        table[key] = value;
                        return;
                    }
                }
                else if (method == null)
                {
                    throw new RuntimeFault("script: expected table or __newindex method");
                }
                if (IsFunction(method))
                {
                    Call(method, target, key, value);
                    return;
                }
                target = method;
            }
            throw new RuntimeFault("script: loop in __newindex");
        }

        internal static bool TryNumber(object? value, out int number)
        {
            if (value is int integer)
            {
                number = integer;
                return true;
            }
            number = 0;
            if (!IsString(value))
            {
                return false;
            }
            string text = Text(value);
            int cursor = 0;
            bool Space(char c) => c == ' ' || c >= '\t' && c <= '\r';
            while (cursor < text.Length && Space(text[cursor]))
            {
                cursor++;
            }
            bool negative = cursor < text.Length && text[cursor] == '-';
            if (cursor < text.Length && (negative || text[cursor] == '+'))
            {
                cursor++;
            }
            int digits = cursor;
            uint maximum = negative ? 0x80000000u : 0x7fffffffu;
            uint magnitude = 0;
            while (cursor < text.Length && text[cursor] >= '0' && text[cursor] <= '9')
            {
                uint digit = (uint)(text[cursor++] - '0');
                magnitude = magnitude > (maximum - digit) / 10 ? maximum : magnitude * 10 + digit;
            }
            if (cursor == digits)
            {
                return false;
            }
            while (cursor < text.Length && Space(text[cursor]))
            {
                cursor++;
            }
            if (cursor < text.Length && text[cursor] != '\0')
            {
                return false;
            }
            number = unchecked((int)(negative ? 0u - magnitude : magnitude));
            return true;
        }

        private object? BinaryMethod(string name, object? left, object? right)
        {
            object? method = Metatable(left)?[name] ?? Metatable(right)?[name];
            if (!IsFunction(method))
            {
                throw new RuntimeFault($"script: missing {name} method");
            }
            return FirstResult(method, left, right);
        }

        private object? Arithmetic(int operation, object? left, object? right)
        {
            if (TryNumber(left, out int a) && TryNumber(right, out int b))
            {
                if (operation == 16)
                {
                    object? power = Globals["__op"];
                    if (!IsFunction(power))
                    {
                        throw new RuntimeFault("script: __op is not a function");
                    }
                    return FirstResult(power, a, b);
                }
                return BoxInteger(operation switch
                {
                    12 => unchecked(a + b),
                    13 => unchecked(a - b),
                    14 => unchecked(a * b),
                    15 => b == 0 ? throw new RuntimeFault("division by zero") : (int)((long)a / b),
                    _ => throw new RuntimeFault("invalid arithmetic operator")
                });
            }
            return BinaryMethod(operation == 12 ? "__add" : operation == 13 ? "__sub" :
                operation == 14 ? "__mul" : operation == 15 ? "__div" : "__op", left, right);
        }

        internal static bool RawEqual(object? left, object? right)
        {
            if (left is ScriptMutableString leftString && leftString.InternedText != null && right is string rightText)
            {
                return rightText == leftString.InternedText && leftString.TextEquals(rightText);
            }
            if (right is ScriptMutableString rightString && rightString.InternedText != null && left is string leftText)
            {
                return leftText == rightString.InternedText && rightString.TextEquals(leftText);
            }
            return IsFunction(left) || IsFunction(right) ? ReferenceEquals(left, right) : Equals(left, right);
        }

        internal bool EqualValue(object? left, object? right)
        {
            if (RawEqual(left, right))
            {
                return true;
            }
            if (left == null || right == null || left.GetType() != right.GetType() ||
                (!(left is ScriptTable) && !(left is ScriptUserdata)))
            {
                return false;
            }
            object? method = Metatable(left)?["__eq"];
            return method != null && RawEqual(method, Metatable(right)?["__eq"]) &&
                Truth(FirstResult(method, left, right));
        }

        internal bool CompareValue(object? left, object? right, bool inclusive)
        {
            if (left is int a && right is int b)
            {
                return inclusive ? a <= b : a < b;
            }
            if (IsString(left) && IsString(right))
            {
                int order = (left, right) switch
                {
                    (ScriptMutableString first, ScriptMutableString second) => first.Bytes.SequenceCompareTo(second.Bytes),
                    (ScriptMutableString first, string second) => ScriptChunk.CompareBytesToText(first.Bytes, second.AsSpan()),
                    (string first, ScriptMutableString second) => -ScriptChunk.CompareBytesToText(second.Bytes, first.AsSpan()),
                    _ => string.CompareOrdinal((string?)left, (string?)right)
                };
                return inclusive ? order <= 0 : order < 0;
            }
            if (left == null || right == null || left.GetType() != right.GetType())
            {
                throw new RuntimeFault("script: comparison of different types");
            }
            object? method = Metatable(left)?[inclusive ? "__le" : "__lt"];
            if (method != null && RawEqual(method, Metatable(right)?[inclusive ? "__le" : "__lt"]))
            {
                return Truth(FirstResult(method, left, right));
            }
            method = Metatable(right)?["__lt"];
            if (inclusive && method != null && RawEqual(method, Metatable(left)?["__lt"]))
            {
                return !Truth(FirstResult(method, right, left));
            }
            throw new RuntimeFault("script: missing comparison method");
        }

        internal object? Concatenate(int first, int last, List<object?> stack) =>
            Concatenate(first, last, stack, null, null);

        private object? Concatenate(int first, int last, Cell[] registers, ScriptPrototype prototype) =>
            Concatenate(first, last, null, registers, prototype);

        private object? Concatenate(int first, int last, List<object?>? stack, Cell[]? registers, ScriptPrototype? prototype)
        {
            static object? ReadOperand(int index, List<object?>? items, Cell[]? cells, ScriptPrototype? source) =>
                cells == null ? items![index] : CheckedRegister(cells, source!, index).Value;

            object? Read(int index) => ReadOperand(index, stack, registers, prototype);

            void Write(int index, object? value)
            {
                if (registers == null)
                {
                    stack![index] = value;
                }
                else
                {
                    CheckedRegister(registers, prototype!, index).Value = value;
                }
            }

            bool ToString(int index)
            {
                object? value = Read(index);
                if (value is int)
                {
                    Write(index, CanonicalString(Text(value)));
                    return true;
                }
                return IsString(value);
            }

            int OperandLength(int index)
            {
                object? value = Read(index);
                return value is ScriptMutableString mutable ? mutable.Length : ((string)value!).Length;
            }

            while (last > first)
            {
                if (!ToString(last - 1) || !ToString(last))
                {
                    Write(last - 1, BinaryMethod("__concat", Read(last - 1), Read(last)));
                    last--;
                    continue;
                }
                long length = OperandLength(last);
                if (length == 0)
                {
                    last--;
                    continue;
                }
                int start = last;
                while (start > first && ToString(start - 1))
                {
                    length += OperandLength(--start);
                    if (length > Package.MaxBytes)
                    {
                        throw new RuntimeFault("script string size limit");
                    }
                }
                EnsureStringBuffer((int)length);
                string joined = string.Create((int)length, (stack, registers, prototype, start, last), static (destination, state) =>
                {
                    for (int index = state.start; index <= state.last; index++)
                    {
                        object? operand = ReadOperand(index, state.stack, state.registers, state.prototype);
                        int written;
                        if (operand is ScriptMutableString mutable)
                        {
                            written = ScriptChunk.Bytes.GetChars(mutable.Bytes, destination);
                        }
                        else
                        {
                            string text = (string)operand!;
                            text.AsSpan().CopyTo(destination);
                            written = text.Length;
                        }
                        destination = destination.Slice(written);
                    }
                });
                Write(start, CanonicalString(joined));
                CollectionRequested?.Invoke();
                last = start;
            }
            return Read(first);
        }

        private void RegisterMetatables()
        {
            Globals["_getTab"] = new Native(args =>
            {
                if (args.Length == 0)
                {
                    throw new RuntimeFault("getmetatable requires a value");
                }
                ScriptTable? table = Metatable(args[0]);
                return new object?[] { table?["__metatable"] ?? table };
            });
            Globals["_setTab"] = new Native(args =>
            {
                if (args.Length < 2 || !(args[0] is ScriptTable table) || (args[1] != null && !(args[1] is ScriptTable)))
                {
                    throw new RuntimeFault("setmetatable requires table and nil/table");
                }
                if (table.Metatable?["__metatable"] != null)
                {
                    throw new RuntimeFault("cannot change a protected metatable");
                }
                table.Metatable = args[1] as ScriptTable;
                return new object?[] { table };
            });
        }
    }
}

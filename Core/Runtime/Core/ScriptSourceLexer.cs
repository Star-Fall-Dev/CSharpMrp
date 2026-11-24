using System;
using System.Collections.Generic;
using System.Text;

namespace CSharpMrp
{
    // Independent source front end. MR source is a byte language, like chunk strings.
    internal static partial class ScriptSource
    {
        private sealed class Token
        {
            internal readonly string Kind;
            internal readonly object? Value;
            internal readonly int Line;
            internal readonly int EndLine;

            internal Token(string kind, object? value, int line, int endLine = -1)
            {
                Kind = kind;
                Value = value;
                Line = line;
                EndLine = endLine < 0 ? line : endLine;
            }
        }

        private sealed class Lexer
        {
            private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
            {
                "break", "do", "else", "elif", "end", "false", "for", "def", "if", "in",
                "local", "nil", "repeat", "return", "then", "true", "until", "while"
            };
            private readonly string source;
            private readonly string name;
            private int position;
            private int line = 1;
            private int tokens;

            internal Lexer(byte[] bytes, string name)
            {
                if (bytes.Length > Package.MaxBytes)
                {
                    throw new RuntimeFault($"script {name}: source size limit");
                }
                source = ScriptChunk.Bytes.GetString(bytes);
                this.name = name;
                if (Peek() == '#')
                {
                    while (Peek() >= 0 && Peek() != '\n')
                    {
                        Take();
                    }
                }
            }

            private int Peek(int ahead = 0)
            {
                return position + ahead < source.Length ? source[position + ahead] : -1;
            }

            private int Take()
            {
                int value = Peek();
                if (value >= 0)
                {
                    position++;
                    if (value == '\n')
                    {
                        line++;
                    }
                }
                return value;
            }

            private RuntimeFault Error(string reason)
            {
                return new RuntimeFault($"script {name}:{line}: {reason}");
            }

            private static bool Letter(int value)
            {
                return value >= 'A' && value <= 'Z' || value >= 'a' && value <= 'z' || value == '_';
            }

            private static bool Digit(int value)
            {
                return value >= '0' && value <= '9';
            }

            private static int Hex(int value)
            {
                return Digit(value) ? value - '0' : value >= 'a' && value <= 'f' ? value - 'a' + 10
                    : value >= 'A' && value <= 'F' ? value - 'A' + 10 : -1;
            }

            internal Token Next()
            {
                if (++tokens > 200000)
                {
                    throw Error("source token limit");
                }
                while (true)
                {
                    int next = Peek();
                    if (next is ' ' or '\t' or '\r' or '\n' or '\f' or '\v')
                    {
                        Take();
                    }
                    else if (next == '/' && Peek(1) == '/')
                    {
                        while (Peek() >= 0 && Peek() != '\n')
                        {
                            Take();
                        }
                    }
                    else if (next == '/' && Peek(1) == '*')
                    {
                        Take();
                        Take();
                        int depth = 1;
                        while (depth != 0)
                        {
                            if (Peek() < 0)
                            {
                                throw Error("unfinished comment");
                            }
                            if (Peek() == '/' && Peek(1) == '*')
                            {
                                depth++;
                                Take();
                                Take();
                            }
                            else if (Peek() == '*' && Peek(1) == '/')
                            {
                                depth--;
                                Take();
                                Take();
                            }
                            else
                            {
                                Take();
                            }
                        }
                    }
                    else
                    {
                        break;
                    }
                }
                int start = position;
                int startLine = line;
                int first = Peek();
                if (first < 0)
                {
                    return new Token("eof", null, line);
                }
                if (Letter(first))
                {
                    do
                    {
                        Take();
                    } while (Letter(Peek()) || Digit(Peek()));
                    string word = source.Substring(start, position - start);
                    return new Token(Keywords.Contains(word) ? word : "name", word, startLine);
                }
                if (Digit(first) || first == '.' && Digit(Peek(1)))
                {
                    return new Token("literal", ReadNumber(), startLine);
                }
                if (first is '\'' or '"')
                {
                    return new Token("literal", ReadString(), startLine, line);
                }
                if (first == '[' && Peek(1) == '[')
                {
                    Take();
                    Take();
                    return new Token("literal", ReadLongString(0), startLine, line);
                }
                Take();
                string symbol = ((char)first).ToString();
                if (symbol == "." && Peek() == '.')
                {
                    Take();
                    symbol = "..";
                    if (Peek() == '.')
                    {
                        Take();
                        symbol = "...";
                    }
                }
                else if ((first is '=' or '<' or '>' or '!') && Peek() == '=' ||
                    (first is '&' or '|') && Peek() == first)
                {
                    symbol += (char)Take();
                }
                return new Token(symbol, null, startLine);
            }

            private int ReadNumber()
            {
                int start = position;
                if (Peek() == '0' && Peek(1) is 'x' or 'X')
                {
                    Take();
                    Take();
                    if (Hex(Peek()) < 0)
                    {
                        throw Error("malformed hex number");
                    }
                    uint value = 0;
                    while (Hex(Peek()) >= 0)
                    {
                        uint digit = (uint)Hex(Take());
                        value = value > (uint.MaxValue - digit) / 16 ? uint.MaxValue : value * 16 + digit;
                    }
                    return unchecked((int)value);
                }
                while (Digit(Peek()))
                {
                    Take();
                }
                if (Peek() == '.')
                {
                    Take();
                    if (Peek() == '.')
                    {
                        throw Error("ambiguous decimal point and concatenation");
                    }
                    while (Digit(Peek()))
                    {
                        Take();
                    }
                }
                if (Peek() is 'e' or 'E')
                {
                    Take();
                    if (Peek() is '+' or '-')
                    {
                        Take();
                    }
                    while (Digit(Peek()))
                    {
                        Take();
                    }
                }
                if (!ScriptVm.TryNumber(source.Substring(start, position - start), out int number))
                {
                    throw Error("malformed integer number");
                }
                return number;
            }

            private string ReadLongString(int quote)
            {
                if (Peek() == '\n')
                {
                    Take();
                }
                var result = new StringBuilder();
                int depth = 0;
                while (Peek() >= 0)
                {
                    if (quote != 0 && Peek() == quote && Peek(1) == quote && Peek(2) == quote)
                    {
                        Take();
                        Take();
                        Take();
                        return result.ToString();
                    }
                    if (quote == 0 && Peek() == ']' && Peek(1) == ']')
                    {
                        Take();
                        Take();
                        if (depth-- == 0)
                        {
                            return result.ToString();
                        }
                        result.Append("]]");
                    }
                    else if (quote == 0 && Peek() == '[' && Peek(1) == '[')
                    {
                        depth++;
                        Take();
                        Take();
                        result.Append("[[");
                    }
                    else
                    {
                        result.Append((char)Take());
                    }
                }
                throw Error("unfinished long string");
            }

            private string ReadString()
            {
                int quote = Take();
                if (Peek() == quote && Peek(1) == quote)
                {
                    Take();
                    Take();
                    return ReadLongString(quote);
                }
                var result = new StringBuilder();
                while (Peek() != quote)
                {
                    if (Peek() < 0 || Peek() == '\n')
                    {
                        throw Error("unfinished string");
                    }
                    int value = Take();
                    if (value == '\\')
                    {
                        value = Take();
                        if (value < 0)
                        {
                            throw Error("unfinished string");
                        }
                        if (value is 'x' or 'X')
                        {
                            if (Hex(Peek()) < 0)
                            {
                                throw Error("hex digit expected in escape");
                            }
                            value = Hex(Take());
                            if (Hex(Peek()) >= 0)
                            {
                                value = value * 16 + Hex(Take());
                            }
                        }
                        else if (Digit(value))
                        {
                            value -= '0';
                            for (int i = 0; i < 2 && Digit(Peek()); i++)
                            {
                                value = value * 10 + Take() - '0';
                            }
                            if (value > 255)
                            {
                                throw Error("escape sequence too large");
                            }
                        }
                        else
                        {
                            value = value switch
                            {
                                'a' => 7,
                                'b' => 8,
                                'f' => 12,
                                'n' => 10,
                                'r' => 13,
                                't' => 9,
                                'v' => 11,
                                _ => value
                            };
                        }
                    }
                    result.Append((char)value);
                }
                Take();
                return result.ToString();
            }
        }
    }
}

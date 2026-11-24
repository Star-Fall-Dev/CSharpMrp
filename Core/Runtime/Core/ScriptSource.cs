using System;
using System.Collections.Generic;

namespace CSharpMrp
{
    internal static partial class ScriptSource
    {
        private sealed class Expression
        {
            internal readonly string Kind;
            internal readonly object? Value;
            internal readonly int Line;
            internal int EndLine;
            internal int MethodLine;
            internal readonly List<Expression> Items;
            internal Function? Function;

            internal Expression(string kind, object? value, int line, params Expression[] items)
            {
                Kind = kind;
                Value = value;
                Line = line;
                EndLine = line;
                Items = new List<Expression>(items);
            }
        }

        private sealed class Function
        {
            internal int Line, EndLine;
            internal readonly List<string> Parameters = new List<string>();
            internal bool Vararg;
            internal List<Statement> Body = new List<Statement>();
        }

        private sealed class Statement
        {
            internal int Line, EndLine;
            internal int LoopLine;
            internal bool Definition;
            internal readonly string Kind;
            internal readonly List<Expression> Targets = new List<Expression>();
            internal readonly List<Expression> Values = new List<Expression>();
            internal readonly List<string> Names = new List<string>();
            internal List<Statement> Body = new List<Statement>();

            internal Statement(string kind)
            {
                Kind = kind;
            }
        }

        private sealed class Parser
        {
            private readonly Lexer lexer;
            private readonly string name;
            private Token token;
            private Token? lookahead;
            private int previousLine = 1;
            private int depth;

            internal Parser(byte[] bytes, string name)
            {
                lexer = new Lexer(bytes, name);
                this.name = name;
                token = lexer.Next();
            }

            private RuntimeFault Error(string message)
            {
                return new RuntimeFault($"script {name}:{token.Line}: {message} near {token.Kind}");
            }

            private Token Advance()
            {
                Token result = token;
                previousLine = result.EndLine;
                token = lookahead ?? lexer.Next();
                lookahead = null;
                return result;
            }

            private bool Match(string kind)
            {
                if (token.Kind != kind)
                {
                    return false;
                }
                Advance();
                return true;
            }

            private Token Expect(string kind)
            {
                if (token.Kind != kind)
                {
                    throw Error("expected " + kind);
                }
                return Advance();
            }

            internal ScriptPrototype Parse()
            {
                var function = new Function { Body = Block(), EndLine = previousLine };
                Expect("eof");
                return new Emitter(name).Compile(function);
            }

            private static bool BlockEnd(string kind)
            {
                return kind is "eof" or "end" or "else" or "elif" or "until";
            }

            private void ExpressionList(List<Expression> values)
            {
                do
                {
                    values.Add(ParseExpression());
                } while (Match(","));
            }

            private List<Statement> Block()
            {
                if (++depth > 100)
                {
                    throw Error("source nesting limit");
                }
                var statements = new List<Statement>();
                while (!BlockEnd(token.Kind))
                {
                    int statementLine = token.Line;
                    Statement statement;
                    if (Match("return"))
                    {
                        statement = new Statement("return");
                        if (!BlockEnd(token.Kind) && token.Kind != ";")
                        {
                            ExpressionList(statement.Values);
                        }
                    }
                    else if (Match("break"))
                    {
                        statement = new Statement("break");
                    }
                    else if (Match("if"))
                    {
                        statement = new Statement("if");
                        do
                        {
                            var branch = new Statement("branch");
                            branch.Values.Add(ParseExpression());
                            Expect("then");
                            branch.Body = Block();
                            statement.Body.Add(branch);
                        } while (Match("elif"));
                        if (Match("else"))
                        {
                            statement.Body.Add(new Statement("branch") { Body = Block() });
                        }
                        Expect("end");
                    }
                    else if (Match("while"))
                    {
                        statement = new Statement("while");
                        statement.Values.Add(ParseExpression());
                        Expect("do");
                        statement.Body = Block();
                        Expect("end");
                    }
                    else if (Match("repeat"))
                    {
                        statement = new Statement("repeat") { Body = Block() };
                        Expect("until");
                        statement.Values.Add(ParseExpression());
                    }
                    else if (Match("for"))
                    {
                        Token identifier = Expect("name");
                        bool numeric = Match("=");
                        statement = new Statement(numeric ? "for" : "iterate");
                        statement.Names.Add((string)identifier.Value!);
                        if (numeric)
                        {
                            statement.Values.Add(ParseExpression());
                            Expect(",");
                            statement.Values.Add(ParseExpression());
                            statement.Values.Add(Match(",") ? ParseExpression() : new Expression("literal", 1, token.Line));
                        }
                        else
                        {
                            while (Match(","))
                            {
                                statement.Names.Add((string)Expect("name").Value!);
                            }
                            Expect("in");
                            statement.LoopLine = token.EndLine;
                            ExpressionList(statement.Values);
                        }
                        Expect("do");
                        statement.Body = Block();
                        Expect("end");
                    }
                    else if (Match("local"))
                    {
                        bool definition = Match("def");
                        statement = new Statement(definition ? "localdef" : "local");
                        do
                        {
                            statement.Names.Add((string)Expect("name").Value!);
                        } while (!definition && Match(","));
                        if (definition)
                        {
                            statement.Values.Add(FunctionBody(false));
                        }
                        else if (Match("="))
                        {
                            ExpressionList(statement.Values);
                        }
                    }
                    else if (Match("do"))
                    {
                        statement = new Statement("block") { Body = Block() };
                        Expect("end");
                    }
                    else if (Match("def"))
                    {
                        Token identifier = Expect("name");
                        var target = new Expression("name", identifier.Value, identifier.Line);
                        bool method = false;
                        while (token.Kind is "." or ":")
                        {
                            method = Advance().Kind == ":";
                            Token field = Expect("name");
                            target = new Expression("index", null, field.Line, target,
                                new Expression("literal", field.Value, field.Line));
                            if (method)
                            {
                                break;
                            }
                        }
                        statement = new Statement("assign") { Definition = true };
                        statement.Targets.Add(target);
                        statement.Values.Add(FunctionBody(method, statementLine));
                    }
                    else
                    {
                        Expression first = Primary();
                        statement = new Statement(first.Kind == "call" ? "call" : "assign");
                        if (statement.Kind == "call")
                        {
                            statement.Values.Add(first);
                        }
                        else
                        {
                            statement.Targets.Add(first);
                            while (Match(","))
                            {
                                statement.Targets.Add(Primary());
                            }
                            foreach (Expression target in statement.Targets)
                            {
                                if (target.Kind != "name" && target.Kind != "index")
                                {
                                    throw Error("invalid assignment target");
                                }
                            }
                            Expect("=");
                            ExpressionList(statement.Values);
                        }
                    }
                    statement.Line = statementLine;
                    statement.EndLine = previousLine;
                    statements.Add(statement);
                    Match(";");
                    if (statement.Kind is "return" or "break")
                    {
                        if (!BlockEnd(token.Kind))
                        {
                            throw Error(statement.Kind + " must finish block");
                        }
                        break;
                    }
                }
                depth--;
                return statements;
            }

            private Expression FunctionBody(bool method, int definitionLine = 0)
            {
                int line = token.Line;
                Expect("(");
                var function = new Function { Line = definitionLine == 0 ? line : definitionLine };
                if (method)
                {
                    function.Parameters.Add("self");
                }
                if (!Match(")"))
                {
                    do
                    {
                        if (Match("..."))
                        {
                            function.Vararg = true;
                            break;
                        }
                        function.Parameters.Add((string)Expect("name").Value!);
                    } while (Match(","));
                    Expect(")");
                }
                function.Body = Block();
                Expect("end");
                function.EndLine = previousLine;
                return new Expression("function", null, line) { Function = function, EndLine = previousLine };
            }

            private static int Priority(string op)
            {
                return op switch
                {
                    "||" => 1,
                    "&&" => 2,
                    "==" or "!=" or "<" or ">" or "<=" or ">=" => 3,
                    ".." => 5,
                    "+" or "-" or "&" or "|" or "^" => 6,
                    "*" or "/" => 7,
                    "#" => 10,
                    _ => 0
                };
            }

            private Expression ParseExpression(int minimum = 1)
            {
                if (++depth > 100)
                {
                    throw Error("source nesting limit");
                }
                Expression result;
                if (token.Kind is "-" or "!" or "~")
                {
                    Token unary = Advance();
                    result = new Expression("unary", unary.Kind, unary.Line, ParseExpression(9));
                }
                else
                {
                    result = Primary();
                }
                while (Priority(token.Kind) >= minimum)
                {
                    Token operation = Advance();
                    int priority = Priority(operation.Kind);
                    Expression right = ParseExpression(priority + (operation.Kind is "#" or ".." ? 0 : 1));
                    result = new Expression("binary", operation.Kind, operation.Line, result, right);
                }
                result.EndLine = previousLine;
                depth--;
                return result;
            }

            private Expression Primary()
            {
                Token first = Advance();
                Expression result;
                if (first.Kind == "def")
                {
                    return FunctionBody(false, first.Line);
                }
                if (first.Kind == "{")
                {
                    return Constructor(first.Line);
                }
                if (first.Kind is "literal" or "nil" or "true" or "false")
                {
                    object? value = first.Kind == "literal" ? first.Value : first.Kind == "nil" ? null : first.Kind == "true";
                    return new Expression("literal", value, first.Line);
                }
                if (first.Kind == "name")
                {
                    result = new Expression("name", first.Value, first.Line);
                }
                else if (first.Kind == "(")
                {
                    result = new Expression("group", null, first.Line, ParseExpression());
                    Expect(")");
                    if (result.Items[0].Kind is "index" or "name")
                    {
                        result.Items[0].EndLine = previousLine;
                    }
                }
                else
                {
                    throw Error("expected expression");
                }
                while (true)
                {
                    result.EndLine = previousLine;
                    if (Match("."))
                    {
                        Token field = Expect("name");
                        result = new Expression("index", null, field.Line, result,
                            new Expression("literal", field.Value, field.Line));
                    }
                    else if (Match("["))
                    {
                        result = new Expression("index", null, token.Line, result, ParseExpression());
                        Expect("]");
                    }
                    else if (token.Kind is "(" or ":" or "{" || token.Kind == "literal" && token.Value is string)
                    {
                        Token start = token;
                        string? method = null;
                        if (Match(":"))
                        {
                            method = (string)Expect("name").Value!;
                        }
                        var call = new Expression("call", method, token.EndLine, result) { MethodLine = previousLine };
                        if (token.Kind == "(" && token.Line != previousLine)
                        {
                            throw Error("ambiguous function call across line boundary");
                        }
                        if (Match("("))
                        {
                            if (!Match(")"))
                            {
                                do
                                {
                                    call.Items.Add(ParseExpression());
                                } while (Match(","));
                                Expect(")");
                            }
                        }
                        else if (token.Kind == "literal" && token.Value is string)
                        {
                            Token text = Advance();
                            call.Items.Add(new Expression("literal", text.Value, text.Line));
                        }
                        else if (Match("{"))
                        {
                            call.Items.Add(Constructor(start.Line));
                        }
                        else
                        {
                            throw Error("expected function arguments");
                        }
                        result = call;
                    }
                    else
                    {
                        result.EndLine = previousLine;
                        return result;
                    }
                }
            }

            private Expression Constructor(int line)
            {
                if (++depth > 100)
                {
                    throw Error("source nesting limit");
                }
                var table = new Expression("table", null, line);
                do
                {
                    Match(";");
                    if (token.Kind == "}")
                    {
                        break;
                    }
                    Expression? key = null;
                    if (Match("["))
                    {
                        key = ParseExpression();
                        Expect("]");
                        Expect("=");
                    }
                    else if (token.Kind == "name")
                    {
                        lookahead ??= lexer.Next();
                        if (lookahead.Kind == "=")
                        {
                            Token field = Advance();
                            key = new Expression("literal", field.Value, field.Line);
                            Expect("=");
                        }
                    }
                    Expression value = ParseExpression();
                    table.Items.Add(key == null ? new Expression("item", null, value.Line, value)
                        : new Expression("field", null, value.Line, key, value));
                } while (Match(",") || Match(";"));
                Expect("}");
                depth--;
                return table;
            }
        }

        private sealed class Emitter
        {
            private sealed class Loop
            {
                internal readonly int Locals;
                internal readonly List<int> Breaks = new List<int>();

                internal Loop(int locals)
                {
                    Locals = locals;
                }
            }
            private readonly string name;
            private readonly List<uint> code = new List<uint>();
            private readonly List<int> lines = new List<int>();
            private int currentLine = 1;
            private readonly List<object?> constants = new List<object?>();
            private readonly Dictionary<object, int> constantIndices = new Dictionary<object, int>();
            private int nilConstant = -1;
            private readonly Emitter? parent;
            private readonly List<string> locals = new List<string>();
            private readonly List<ScriptLocal> debugLocals = new List<ScriptLocal>();
            private readonly List<int> localRecords = new List<int>();
            private readonly Dictionary<string, int> captures = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly List<(bool Local, int Index)> captureSources = new List<(bool, int)>();
            private readonly List<ScriptPrototype> children = new List<ScriptPrototype>();
            private readonly ScriptTable compilationTable = new ScriptTable();
            private readonly Stack<Loop> loops = new Stack<Loop>();
            private readonly int functionDepth;
            private int stack = 2;
            private int depth;

            internal Emitter(string name, Emitter? parent = null)
            {
                this.name = name;
                this.parent = parent;
                functionDepth = parent == null ? 0 : parent.functionDepth + 1;
                if (functionDepth > 64)
                {
                    throw new RuntimeFault($"script {name}: source function depth limit");
                }
            }

            private void Register(int register)
            {
                if (register < 0 || register >= 250)
                {
                    throw new RuntimeFault($"script {name}: source register limit");
                }
                stack = Math.Max(stack, register + 1);
            }

            private int Emit(int op, int a = 0, int b = 0, int c = 0)
            {
                if (code.Count >= 100000)
                {
                    throw new RuntimeFault($"script {name}: source instruction limit");
                }
                Register(a);
                code.Add((uint)(op | a << 24 | b << 15 | c << 6));
                lines.Add(currentLine);
                return code.Count - 1;
            }

            private void Wide(int op, int a, int bx)
            {
                Emit(op, a, bx >> 9, bx & 511);
            }

            private int Constant(object? value)
            {
                if (value == null)
                {
                    if (nilConstant < 0)
                    {
                        nilConstant = constants.Count;
                        constants.Add(null);
                        compilationTable[compilationTable] = nilConstant;
                    }
                    return nilConstant;
                }
                if (!constantIndices.TryGetValue(value, out int index))
                {
                    index = constants.Count;
                    if (index >= 100000)
                    {
                        throw new RuntimeFault($"script {name}: source constant limit");
                    }
                    constants.Add(value);
                    compilationTable[value] = index;
                    constantIndices.Add(value, index);
                }
                return index;
            }

            private void Patch(int instruction, int target)
            {
                int offset = target - instruction - 1 + 131071;
                if (offset < 0 || offset > 262143)
                {
                    throw new RuntimeFault($"script {name}: source jump limit");
                }
                code[instruction] = code[instruction] & 0xff00003fu | (uint)offset << 6;
            }

            internal ScriptPrototype Compile(Function function)
            {
                if (function.Parameters.Count > 100)
                {
                    throw new RuntimeFault($"script {name}: source parameter limit");
                }
                foreach (string parameter in function.Parameters)
                {
                    AddLocal(parameter);
                }
                if (function.Vararg)
                {
                    AddLocal("arg");
                }
                Statements(function.Body);
                CloseLocals(0);
                currentLine = function.EndLine;
                Emit(27, 0, 1);
                var upvalueNames = new string?[captureSources.Count];
                foreach (var capture in captures)
                {
                    upvalueNames[capture.Value] = capture.Key;
                }
                return new ScriptPrototype
                {
                    CompilationTable = compilationTable,
                    Source = name,
                    LineDefined = function.Line,
                    Lines = lines.ToArray(),
                    Locals = debugLocals.ToArray(),
                    UpvalueNames = upvalueNames,
                    Parameters = function.Parameters.Count,
                    Vararg = function.Vararg,
                    Upvalues = captureSources.Count,
                    Stack = stack,
                    Constants = constants.ToArray(),
                    Children = children.ToArray(),
                    Code = code.ToArray()
                };
            }

            private void AddLocal(string identifier)
            {
                if (locals.Count >= 200)
                {
                    throw new RuntimeFault($"script {name}: source local variable limit");
                }
                Register(locals.Count);
                locals.Add(identifier);
                localRecords.Add(debugLocals.Count);
                debugLocals.Add(new ScriptLocal(identifier, code.Count, code.Count));
            }

            private void CloseLocals(int start)
            {
                for (int i = start; i < locals.Count; i++)
                {
                    int index = localRecords[i];
                    ScriptLocal local = debugLocals[index];
                    debugLocals[index] = new ScriptLocal(local.Name, local.Start, code.Count);
                }
                localRecords.RemoveRange(start, locals.Count - start);
                locals.RemoveRange(start, locals.Count - start);
            }

            private (string Kind, int Index) Resolve(string identifier)
            {
                int local = locals.LastIndexOf(identifier);
                if (local >= 0)
                {
                    return ("local", local);
                }
                if (captures.TryGetValue(identifier, out int captured))
                {
                    return ("capture", captured);
                }
                if (parent != null)
                {
                    var binding = parent.Resolve(identifier);
                    if (binding.Kind != "global")
                    {
                        int index = captureSources.Count;
                        if (index >= 32)
                        {
                            throw new RuntimeFault($"script {name}: source upvalue limit");
                        }
                        captureSources.Add((binding.Kind == "local", binding.Index));
                        captures.Add(identifier, index);
                        return ("capture", index);
                    }
                }
                return ("global", Constant(identifier));
            }

            private void Scope(List<Statement> statements)
            {
                int start = locals.Count;
                Statements(statements);
                if (locals.Count > start)
                {
                    CloseLocals(start);
                    Emit(33, start);
                }
            }

            private void Values(List<Expression> expressions, int output, int wanted)
            {
                int produced = 0;
                for (int i = 0; i < expressions.Count; i++)
                {
                    int results = i == expressions.Count - 1 && expressions[i].Kind == "call"
                        ? Math.Max(0, wanted - i) : 1;
                    Value(expressions[i], output + i, results);
                    produced += results;
                }
                for (int i = produced; i < wanted; i++)
                {
                    Emit(3, output + i, output + i);
                }
            }

            private void Statements(List<Statement> statements)
            {
                foreach (Statement statement in statements)
                {
                    int previousLine = currentLine;
                    currentLine = statement.EndLine;
                    int start = locals.Count;
                    switch (statement.Kind)
                    {
                        case "return":
                            Return(statement.Values, start);
                            break;
                        case "call":
                            Value(statement.Values[0], start, 0);
                            break;
                        case "block":
                            Scope(statement.Body);
                            break;
                        case "if":
                            var ends = new List<int>();
                            foreach (Statement branch in statement.Body)
                            {
                                int failure = branch.Values.Count == 0 ? -1 : ConditionJumps(branch.Values[0], start, jumpOnTrue: false);
                                Scope(branch.Body);
                                ends.Add(Emit(20));
                                PatchConditionJumps(failure, code.Count);
                            }
                            foreach (int end in ends)
                            {
                                Patch(end, code.Count);
                            }
                            break;
                        case "while":
                        case "repeat":
                        case "for":
                        case "iterate":
                            Iteration(statement);
                            break;
                        case "break":
                            if (loops.Count == 0)
                            {
                                throw new RuntimeFault($"script {name}: break outside loop");
                            }
                            Loop loop = loops.Peek();
                            if (locals.Count > loop.Locals)
                            {
                                Emit(33, loop.Locals);
                            }
                            loop.Breaks.Add(Emit(20));
                            break;
                        case "local":
                            Values(statement.Values, start, statement.Names.Count);
                            foreach (string identifier in statement.Names)
                            {
                                AddLocal(identifier);
                            }
                            break;
                        case "localdef":
                            AddLocal(statement.Names[0]);
                            Emit(3, start, start);
                            Value(statement.Values[0], start);
                            debugLocals[localRecords[start]] = new ScriptLocal(statement.Names[0], code.Count, code.Count);
                            break;
                        case "assign":
                            var targets = new List<(string Kind, int Index, int Key)>();
                            int temporary = start;
                            foreach (Expression target in statement.Targets)
                            {
                                if (target.Kind == "name")
                                {
                                    var binding = Resolve((string)target.Value!);
                                    bool saved = false;
                                    for (int i = 0; binding.Kind == "local" && i < targets.Count; i++)
                                    {
                                        var previous = targets[i];
                                        if (previous.Kind == "index" &&
                                            (previous.Index == binding.Index || previous.Key == binding.Index))
                                        {
                                            if (!saved)
                                            {
                                                Emit(0, temporary, binding.Index);
                                                saved = true;
                                            }
                                            targets[i] = (previous.Kind,
                                                previous.Index == binding.Index ? temporary : previous.Index,
                                                previous.Key == binding.Index ? temporary : previous.Key);
                                        }
                                    }
                                    if (saved)
                                    {
                                        temporary++;
                                    }
                                    targets.Add((binding.Kind, binding.Index, 0));
                                }
                                else
                                {
                                    int table = Operand(target.Items[0], ref temporary, false);
                                    int key = Operand(target.Items[1], ref temporary);
                                    targets.Add(("index", table, key));
                                }
                            }
                            bool direct = statement.Values.Count == targets.Count &&
                                targets[targets.Count - 1].Kind == "local";
                            if (direct)
                            {
                                for (int i = 0; i < statement.Values.Count - 1; i++)
                                {
                                    Value(statement.Values[i], temporary + i);
                                }
                                AssignValue(statement.Values[statement.Values.Count - 1],
                                    temporary + targets.Count - 1, targets[targets.Count - 1].Index);
                            }
                            else
                            {
                                Values(statement.Values, temporary, targets.Count);
                            }
                            for (int i = targets.Count - 1; i >= 0; i--)
                            {
                                if (statement.Definition)
                                {
                                    currentLine = statement.Line;
                                }
                                var target = targets[i];
                                if (target.Kind == "local")
                                {
                                    if (!direct || i != targets.Count - 1)
                                    {
                                        Emit(0, target.Index, temporary + i);
                                    }
                                }
                                else if (target.Kind == "capture")
                                {
                                    Emit(8, temporary + i, target.Index);
                                }
                                else if (target.Kind == "global")
                                {
                                    Wide(7, temporary + i, target.Index);
                                }
                                else
                                {
                                    Emit(9, target.Index, target.Key, temporary + i);
                                }
                            }
                            break;
                        default:
                            throw new RuntimeFault($"script {name}: unsupported source statement {statement.Kind}");
                    }
                    currentLine = previousLine;
                }
            }

            private int ConditionJumps(Expression condition, int output, bool jumpOnTrue, int previous = -1)
            {
                Expression test = Ungroup(condition);
                if (test.Kind == "unary" && test.Value is "!")
                {
                    EnterExpression(test);
                    int inverted = ConditionJumps(test.Items[0], output, !jumpOnTrue, previous);
                    depth--;
                    return inverted;
                }
                if (test.Kind == "binary" && test.Value is "&&" or "||")
                {
                    EnterExpression(test);
                    Expression left = Ungroup(test.Items[0]);
                    Expression right = test.Items[1];
                    bool conjunction = test.Value is "&&";
                    int jumps;
                    if (left.Kind == "literal" && Truthy(left.Value) == conjunction)
                    {
                        jumps = ConditionJumps(right, output, jumpOnTrue, previous);
                    }
                    else if (jumpOnTrue != conjunction)
                    {
                        int first = ConditionJumps(left, output, jumpOnTrue, previous);
                        jumps = ConditionJumps(right, output, jumpOnTrue, first);
                    }
                    else
                    {
                        int skipRight = ConditionJumps(left, output, !jumpOnTrue);
                        jumps = ConditionJumps(right, output, jumpOnTrue, previous);
                        PatchConditionJumps(skipRight, code.Count);
                    }
                    depth--;
                    return jumps;
                }
                int opcode = test.Kind == "binary" ? ComparisonOpcode(test.Value as string) : 0;
                if (opcode != 0)
                {
                    int previousLine = currentLine;
                    currentLine = test.EndLine;
                    EmitComparison(test, output, opcode, jumpOnTrue);
                    currentLine = previousLine;
                }
                else
                {
                    int temporary = output;
                    int register = Operand(test, ref temporary, false);
                    Emit(24, register, register, jumpOnTrue ? 1 : 0);
                }
                // Until patched, the jump operand links to the previous pending jump (zero ends the list).
                int jump = Emit(20);
                code[jump] |= (uint)(previous + 1) << 6;
                return jump;
            }

            private void PatchConditionJumps(int jump, int target)
            {
                while (jump >= 0)
                {
                    int previous = (int)((code[jump] >> 6) & 0x3ffff) - 1;
                    Patch(jump, target);
                    jump = previous;
                }
            }

            private static int ComparisonOpcode(string? op) => op switch
            {
                "==" or "!=" => 21,
                "<" or ">" => 22,
                "<=" or ">=" => 23,
                _ => 0
            };

            private void EmitComparison(Expression expression, int output, int opcode, bool jumpOnTrue)
            {
                int temporary = output;
                int left = Operand(expression.Items[0], ref temporary);
                int right = Operand(expression.Items[1], ref temporary);
                bool reverse = expression.Value is ">" or ">=";
                bool expected = jumpOnTrue != (expression.Value is "!=");
                Emit(opcode, expected ? 1 : 0, reverse ? right : left, reverse ? left : right);
            }

            private void AssignValue(Expression expression, int temporary, int destination)
            {
                expression = Ungroup(expression);
                if (expression.Kind == "binary" && expression.Value is "&&" or "||")
                {
                    bool conjunction = expression.Value as string == "&&";
                    Expression left = Ungroup(expression.Items[0]);
                    if (left.Kind == "literal" && Truthy(left.Value) == conjunction)
                    {
                        AssignValue(expression.Items[1], temporary, destination);
                        return;
                    }
                    int next = temporary;
                    int test = Operand(left, ref next, false);
                    Emit(24, destination, test, conjunction ? 0 : 1);
                    int skip = Emit(20);
                    AssignValue(expression.Items[1], temporary, destination);
                    Patch(skip, code.Count);
                    return;
                }
                Value(expression, temporary);
                uint instruction = code[code.Count - 1];
                int opcode = (int)(instruction & 63);
                if (instruction >> 24 == temporary &&
                    (opcode == 6 || opcode >= 12 && opcode <= 19 || opcode >= 35 && opcode <= 38))
                {
                    code[code.Count - 1] = instruction & 0x00ffffffu | (uint)destination << 24;
                }
                else
                {
                    Emit(0, destination, temporary);
                }
            }

            private void Jump(int target)
            {
                Patch(Emit(20), target);
            }

            private void Iteration(Statement statement)
            {
                currentLine = statement.Values[statement.Values.Count - 1].EndLine;
                int start = locals.Count;
                var loop = new Loop(start);
                loops.Push(loop);
                if (statement.Kind == "while")
                {
                    int head = code.Count;
                    int end = ConditionJumps(statement.Values[0], start, jumpOnTrue: false);
                    Scope(statement.Body);
                    Jump(head);
                    PatchConditionJumps(end, code.Count);
                }
                else if (statement.Kind == "repeat")
                {
                    int head = code.Count;
                    // MR's repeat block ends before its condition (the 5.0 dialect).
                    Scope(statement.Body);
                    PatchConditionJumps(ConditionJumps(statement.Values[0], start, jumpOnTrue: false), head);
                }
                else
                {
                    bool numeric = statement.Kind == "for";
                    Values(statement.Values, start, numeric ? 3 : statement.Names.Count + 2);
                    if (numeric)
                    {
                        AddLocal(statement.Names[0]);
                        AddLocal("(for limit)");
                        AddLocal("(for step)");
                        Emit(13, start, start, start + 2);
                    }
                    else
                    {
                        AddLocal("(for generator)");
                        AddLocal("(for state)");
                        foreach (string identifier in statement.Names)
                        {
                            AddLocal(identifier);
                        }
                        Register(start + statement.Names.Count + 4);
                    }
                    int enter = Emit(numeric ? 20 : 30, numeric ? 0 : start);
                    int body = code.Count;
                    Scope(statement.Body);
                    Patch(enter, code.Count);
                    if (numeric)
                    {
                        currentLine = statement.Line;
                        Patch(Emit(28, start), body);
                    }
                    else
                    {
                        currentLine = statement.LoopLine;
                        Emit(29, start, 0, statement.Names.Count - 1);
                        Jump(body);
                    }
                    CloseLocals(start);
                    Emit(33, start);
                }
                loops.Pop();
                foreach (int exit in loop.Breaks)
                {
                    Patch(exit, code.Count);
                }
            }

            private void Return(List<Expression> values, int output)
            {
                bool open = false;
                for (int i = 0; i < values.Count; i++)
                {
                    open = i == values.Count - 1 && values[i].Kind == "call";
                    Value(values[i], output + i, open ? -1 : 1);
                }
                if (open && values.Count == 1)
                {
                    code[code.Count - 1] = code[code.Count - 1] & ~63u | 26;
                }
                Emit(27, output, open ? 0 : values.Count + 1);
            }

            private void EnterExpression(Expression expression)
            {
                if (++depth > 100)
                {
                    throw new RuntimeFault($"script {name}:{expression.Line}: source nesting limit");
                }
            }

            private void Value(Expression expression, int output, int returns = 1)
            {
                EnterExpression(expression);
                Register(output);
                int previousLine = currentLine;
                currentLine = expression.Kind == "call" ? expression.Line : expression.EndLine;
                switch (expression.Kind)
                {
                    case "literal":
                        if (expression.Value == null)
                        {
                            Emit(3, output, output);
                        }
                        else if (expression.Value is bool boolean)
                        {
                            Emit(2, output, boolean ? 1 : 0);
                        }
                        else
                        {
                            Wide(1, output, Constant(expression.Value));
                        }
                        break;
                    case "name":
                        var binding = Resolve((string)expression.Value!);
                        if (binding.Kind == "global")
                        {
                            Wide(5, output, binding.Index);
                        }
                        else
                        {
                            Emit(binding.Kind == "local" ? 0 : 4, output, binding.Index);
                        }
                        break;
                    case "function":
                        var child = new Emitter(name, this);
                        ScriptPrototype prototype = child.Compile(expression.Function!);
                        Wide(34, output, children.Count);
                        children.Add(prototype);
                        foreach (var capture in child.captureSources)
                        {
                            Emit(capture.Local ? 0 : 4, 0, capture.Index);
                        }
                        break;
                    case "group":
                        Value(expression.Items[0], output);
                        break;
                    case "index":
                        int temporary = output;
                        int table = Operand(expression.Items[0], ref temporary, false);
                        int key = Operand(expression.Items[1], ref temporary);
                        Emit(6, output, table, key);
                        break;
                    case "unary":
                        int unaryTemporary = output;
                        int operand = Operand(expression.Items[0], ref unaryTemporary, false);
                        Emit(expression.Value as string == "-" ? 17 : expression.Value as string == "!" ? 18 : 35,
                            output, operand);
                        break;
                    case "binary":
                        Binary(expression, output);
                        break;
                    case "call":
                        Call(expression, output, returns);
                        break;
                    case "table":
                        Table(expression, output);
                        break;
                    default:
                        throw new RuntimeFault($"script {name}: unsupported source expression {expression.Kind}");
                }
                currentLine = previousLine;
                depth--;
            }

            private void Table(Expression expression, int output)
            {
                int arrays = 0;
                int records = 0;
                foreach (Expression field in expression.Items)
                {
                    if (field.Kind == "field")
                    {
                        records++;
                    }
                    else
                    {
                        arrays++;
                    }
                }
                int mantissa = arrays;
                int exponent = 0;
                while (mantissa >= 8)
                {
                    mantissa = (mantissa + 1) >> 1;
                    exponent++;
                }
                int hashBits = 0;
                for (int count = records; count > 0; count >>= 1)
                {
                    hashBits++;
                }
                Emit(10, output, (exponent << 3) | mantissa, hashBits);
                int pending = 0;
                int total = 0;
                for (int i = 0; i < expression.Items.Count; i++)
                {
                    if (pending == 32)
                    {
                        Wide(31, output, total - 1);
                        pending = 0;
                    }
                    Expression field = expression.Items[i];
                    if (field.Kind == "field")
                    {
                        int temporary = output + pending + 1;
                        int key = Operand(field.Items[0], ref temporary);
                        int value = Operand(field.Items[1], ref temporary);
                        Emit(9, output, key, value);
                    }
                    else
                    {
                        pending++;
                        total++;
                        bool open = i == expression.Items.Count - 1 && field.Items[0].Kind == "call";
                        Value(field.Items[0], output + pending, open ? -1 : 1);
                        if (open)
                        {
                            Wide(32, output, total - 1);
                            pending = 0;
                        }
                    }
                }
                if (pending != 0)
                {
                    Wide(31, output, total - 1);
                }
            }

            private void Binary(Expression expression, int output)
            {
                string op = (string)expression.Value!;
                if (op is "&&" or "||")
                {
                    Expression leftExpression = Ungroup(expression.Items[0]);
                    if (leftExpression.Kind == "literal" &&
                        (Truthy(leftExpression.Value) == (op == "&&")))
                    {
                        Value(expression.Items[1], output);
                        return;
                    }
                    int testTemporary = output;
                    int test = Operand(leftExpression, ref testTemporary, false);
                    Emit(24, output, test, op == "&&" ? 0 : 1);
                    int skip = Emit(20);
                    Value(expression.Items[1], output);
                    Patch(skip, code.Count);
                    return;
                }
                if (op == "..")
                {
                    Value(expression.Items[0], output);
                    Expression suffix = expression.Items[1];
                    Value(suffix, output + 1);
                    if (suffix.Kind == "binary" && suffix.Value is "..")
                    {
                        // Keep the opcode and last operand, extending the range and moving the result to output.
                        int instruction = code.Count - 1;
                        code[instruction] = code[instruction] & 0x7fffu | (uint)(output << 24 | output << 15);
                        lines[instruction] = currentLine;
                    }
                    else
                    {
                        Emit(19, output, output, output + 1);
                    }
                    return;
                }
                int opcode = op switch
                {
                    "+" => 12,
                    "-" => 13,
                    "*" => 14,
                    "/" => 15,
                    "#" => 16,
                    "&" => 36,
                    "|" => 37,
                    "^" => 38,
                    _ => ComparisonOpcode(op)
                };
                if (opcode is 21 or 22 or 23)
                {
                    EmitComparison(expression, output, opcode, jumpOnTrue: true);
                    int whenTrue = Emit(20);
                    Emit(2, output, 0, 1);
                    Patch(whenTrue, code.Count);
                    Emit(2, output, 1);
                }
                else
                {
                    int temporary = output;
                    int left = Operand(expression.Items[0], ref temporary);
                    int right = Operand(expression.Items[1], ref temporary);
                    Emit(opcode, output, left, right);
                }
            }

            private int Operand(Expression expression, ref int temporary, bool constant = true)
            {
                expression = Ungroup(expression);
                while (expression.Kind == "binary" && expression.Value is "&&" or "||")
                {
                    Expression left = Ungroup(expression.Items[0]);
                    if (left.Kind != "literal" || Truthy(left.Value) != (expression.Value as string == "&&"))
                    {
                        break;
                    }
                    expression = Ungroup(expression.Items[1]);
                }
                if (expression.Kind == "unary" && expression.Value is string operation &&
                    (operation == "-" || operation == "~") &&
                    expression.Items[0].Kind == "literal" && expression.Items[0].Value is int number)
                {
                    expression = new Expression("literal", operation == "-" ? unchecked(-number) : ~number,
                        expression.Line);
                }
                if (expression.Kind == "name")
                {
                    var binding = Resolve((string)expression.Value!);
                    if (binding.Kind == "local")
                    {
                        return binding.Index;
                    }
                }
                if (constant && expression.Kind == "literal" && expression.Value is not bool)
                {
                    int index = Constant(expression.Value);
                    if (index <= 261)
                    {
                        return 250 + index;
                    }
                }
                int register = temporary++;
                Value(expression, register);
                return register;
            }

            private static Expression Ungroup(Expression expression)
            {
                while (expression.Kind == "group")
                {
                    expression = expression.Items[0];
                }
                return expression;
            }

            private static bool Truthy(object? value)
            {
                return value != null && !(value is bool boolean && !boolean);
            }

            private void Call(Expression expression, int output, int returns)
            {
                Value(expression.Items[0], output);
                int offset = 1;
                if (expression.Value is string method)
                {
                    currentLine = expression.MethodLine;
                    Value(new Expression("literal", method, expression.Line), output + 2);
                    Register(output + 1);
                    Emit(11, output, output, output + 2);
                    currentLine = expression.Line;
                    offset++;
                }
                bool open = false;
                for (int i = 1; i < expression.Items.Count; i++)
                {
                    open = i == expression.Items.Count - 1 && expression.Items[i].Kind == "call";
                    Value(expression.Items[i], output + offset + i - 1, open ? -1 : 1);
                }
                if (returns > 0)
                {
                    Register(output + returns - 1);
                }
                Emit(25, output, open ? 0 : expression.Items.Count + offset - 1, returns + 1);
            }
        }

        internal static ScriptPrototype Compile(byte[] bytes, string name)
        {
            return new Parser(bytes, name).Parse();
        }
    }
}

using System;
using System.IO;

namespace CSharpMrp
{
    public static partial class ScriptChunk
    {
        public static byte[] Write(ScriptPrototype prototype)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(new byte[] { 27, (byte)'M', (byte)'R', (byte)'P', 0x80, 1 });
            int elements = 0;
            void Count(int count)
            {
                if (count < 0 || count > 100000 || (long)elements + count > 200000 || stream.Length > Package.MaxBytes - 4)
                {
                    throw new RuntimeFault("script dump element/size limit");
                }
                elements += count;
                writer.Write(count);
            }
            void Text(string? text)
            {
                if (text == null)
                {
                    writer.Write(0);
                    return;
                }
                if ((long)text.Length + stream.Length + 5 > Package.MaxBytes)
                {
                    throw new RuntimeFault("script dump size limit");
                }
                byte[] bytes = Bytes.GetBytes(text);
                writer.Write(bytes.Length + 1);
                writer.Write(bytes);
                writer.Write((byte)0);
            }
            void Function(ScriptPrototype function, int depth, string? parentSource)
            {
                if (depth > 64 || function.Stack < 1 || function.Stack > 250 || function.Parameters < 0 ||
                    function.Parameters > function.Stack || function.Upvalues < 0 || function.Upvalues > 255 ||
                    function.LineDefined < 0 || (function.Lines.Length != 0 && function.Lines.Length != function.Code.Length) ||
                    (function.UpvalueNames.Length != 0 && function.UpvalueNames.Length != function.Upvalues))
                {
                    throw new RuntimeFault("script dump invalid prototype");
                }
                string source = function.CurrentSource;
                Text(source == parentSource ? null : source);
                writer.Write(function.LineDefined);
                writer.Write((byte)function.Upvalues);
                writer.Write((byte)function.Parameters);
                writer.Write(function.Vararg ? (byte)1 : (byte)0);
                writer.Write((byte)function.Stack);
                Count(function.Lines.Length);
                foreach (int line in function.Lines)
                {
                    writer.Write(line);
                }
                Count(function.Locals.Length);
                for (int index = 0; index < function.Locals.Length; index++)
                {
                    ScriptLocal local = function.Locals[index];
                    if (local.Start < 0 || local.End < 0)
                    {
                        throw new RuntimeFault("script dump invalid local range");
                    }
                    Text(function.LocalName(index));
                    writer.Write(local.Start);
                    writer.Write(local.End);
                }
                Count(function.UpvalueNames.Length);
                for (int index = 0; index < function.UpvalueNames.Length; index++)
                {
                    Text(function.UpvalueName(index));
                }
                Count(function.Constants.Length);
                foreach (object? value in function.Constants)
                {
                    if (value == null)
                    {
                        writer.Write((byte)0);
                    }
                    else if (value is int integer)
                    {
                        writer.Write((byte)3);
                        writer.Write(integer);
                    }
                    else if (ScriptVm.IsString(value))
                    {
                        writer.Write((byte)4);
                        Text(ScriptVm.Text(value));
                    }
                    else
                    {
                        throw new RuntimeFault("script dump unsupported constant type");
                    }
                }
                Count(function.Children.Length);
                foreach (ScriptPrototype child in function.Children)
                {
                    Function(child, depth + 1, source);
                }
                Count(function.Code.Length);
                if (stream.Length + (long)function.Code.Length * 4 > Package.MaxBytes)
                {
                    throw new RuntimeFault("script dump size limit");
                }
                foreach (uint instruction in function.Code)
                {
                    writer.Write(instruction);
                }
            }
            Function(prototype, 0, null);
            return stream.ToArray();
        }
    }
}

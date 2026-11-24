using System;

namespace CSharpMrp
{
    public sealed partial class ScriptVm
    {
        // Calls reuse these bounded slots. Clear each slot on unwind so it does not retain closures.
        private readonly object?[] environmentFunctions = new object?[64];
        private readonly int[] environmentTailCalls = new int[64];
        private readonly int[] environmentInstructions = new int[64];
        private int environmentFrameCount;

        internal string ErrorLocation(int level)
        {
            int frame = environmentFrameCount - 1;
            while (level > 0 && frame >= 0)
            {
                level--;
                level -= environmentTailCalls[frame--];
            }
            if (level != 0 || frame < 0 || !(environmentFunctions[frame] is Closure closure))
            {
                return "";
            }
            var prototype = closure.Prototype;
            int instruction = environmentInstructions[frame];
            int line = instruction < prototype.Lines.Length ? prototype.Lines[instruction] : 0;
            if (line <= 0)
            {
                return "";
            }
            string source = prototype.CurrentSource;
            int terminator = source.IndexOf('\0');
            if (terminator >= 0)
            {
                source = source.Substring(0, terminator);
            }
            string display;
            if (source.StartsWith("=", StringComparison.Ordinal))
            {
                display = source.Substring(1, Math.Min(59, source.Length - 1));
            }
            else if (source.StartsWith("@", StringComparison.Ordinal))
            {
                display = source.Length <= 53 ? source.Substring(1) : "..." + source.Substring(source.Length - 52);
            }
            else
            {
                int newline = source.IndexOf('\n');
                int length = Math.Min(43, newline < 0 ? source.Length : newline);
                display = "[string \"" + source.Substring(0, length) + (length < source.Length ? "..." : "") + "\"]";
            }
            return display + ":" + line.ToString(System.Globalization.CultureInfo.InvariantCulture) + ": ";
        }

        internal string CurrentScriptLocation()
        {
            for (int frame = environmentFrameCount - 1; frame >= 0; frame--)
            {
                if (environmentFunctions[frame] is Closure)
                {
                    return ErrorLocation(environmentFrameCount - 1 - frame);
                }
            }
            return "";
        }

        private object EnvironmentFunction(object?[] args)
        {
            object? argument = args.Length == 0 ? null : args[0];
            if (IsFunction(argument))
            {
                return argument!;
            }
            int level = argument == null ? 1 : Number(argument);
            if (level < 0)
            {
                throw new RuntimeFault("environment level must be non-negative");
            }
            int remainingLevel = level;
            int frame = environmentFrameCount - 1;
            while (remainingLevel > 0 && frame >= 0)
            {
                remainingLevel--;
                remainingLevel -= environmentTailCalls[frame];
                frame--;
            }
            if (frame < 0 || remainingLevel > 0)
            {
                throw new RuntimeFault("invalid environment level");
            }
            if (remainingLevel < 0)
            {
                throw new RuntimeFault($"no function environment for tail call at level {level}");
            }
            return environmentFunctions[frame]!;
        }

        private void RegisterEnvironments()
        {
            Globals["_getEnv"] = new Native(args =>
            {
                ScriptTable environment = FunctionEnvironment(EnvironmentFunction(args));
                return new object?[] { environment["__fenv"] ?? environment };
            });
            Globals["_setEnv"] = new Native(args =>
            {
                if (args.Length < 2 || !(args[1] is ScriptTable environment))
                {
                    throw new RuntimeFault("setenv requires an environment table");
                }
                object function = EnvironmentFunction(args);
                if (FunctionEnvironment(function)["__fenv"] != null)
                {
                    throw new RuntimeFault("setenv cannot change a protected environment");
                }
                if (TryNumber(args[0], out int level) && level == 0)
                {
                    Globals = environment;
                }
                else if (!SetFunctionEnvironment(function, environment))
                {
                    throw new RuntimeFault("setenv cannot change environment of given function");
                }
                return Array.Empty<object?>();
            });
        }
    }
}

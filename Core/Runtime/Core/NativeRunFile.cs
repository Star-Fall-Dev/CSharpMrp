using System;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private RuntimeSession? successor;

        // Ownership transfers to the host after a timer-driven RunFile transition.
        public RuntimeSession? TakeSuccessor()
        {
            RuntimeSession? result = successor;
            successor = null;
            return result;
        }

        private object?[] ScheduleRunFile(object?[] args)
        {
            if (args.Length < 2)
            {
                throw new RuntimeFault("RunFile requires package and startup resource names");
            }
            string name = ScriptVm.Text(args[0]);
            string script = ScriptVm.Text(args[1]);
            string parameter = args.Length > 2 && args[2] != null ? ScriptVm.Text(args[2]) : "";
            if (name.Length == 0 || script.Length == 0)
            {
                throw new RuntimeFault("RunFile requires nonempty names");
            }
            WriteRuntimeText(100, name);
            WriteRuntimeText(101, script);
            WriteRuntimeText(138, parameter);
            Memory.Put32(timerFields, ScriptStringPointer("restart"));
            Memory.Put32(timerFields + 4, 1);
            Memory.Put32(runtimeState, 3);
            timerDelay = 100;
            TimerGeneration++;
            Record($"RunFile scheduled: {name}, {script}, delay=100");
            return Array.Empty<object?>();
        }

        private void RestartRequestedFile()
        {
            string name = Memory.CString(Memory.U32(table + 100 * 4));
            string script = Memory.CString(Memory.U32(table + 101 * 4));
            string parameter = Memory.CString(Memory.U32(table + 138 * 4));
            string oldPackage = Memory.CString(Memory.U32(table + 102 * 4));
            string oldScript = Memory.CString(Memory.U32(table + 103 * 4));
            Package selected;
            byte[]? resident = null;
            uint residentIndex = 0;
            if (name.StartsWith("*", StringComparison.Ordinal) || name.StartsWith("$", StringComparison.Ordinal))
            {
                uint pointer;
                if (name[0] == '*')
                {
                    residentIndex = name.Length < 2 ? uint.MaxValue : (uint)(name[1] - 'A');
                    if (residentIndex >= 50)
                    {
                        throw new RuntimeFault("RunFile registered package index out of range");
                    }
                    pointer = Memory.U32(registeredPackages + residentIndex * 4);
                    if (pointer == 0)
                    {
                        throw new RuntimeFault($"RunFile package unavailable: {name} is not registered");
                    }
                    resident = ReadRegisteredPackage(pointer);
                }
                else
                {
                    pointer = Memory.U32(Memory.U32(table + 104 * 4));
                    uint size = Memory.U32(Memory.U32(table + 105 * 4));
                    resident = ReadResidentPackage(pointer, size);
                }
                selected = Package.FromOwnedBytes(resident, true);
            }
            else
            {
                byte[] image = ReadRuntimeFile(name) ?? throw new RuntimeFault($"RunFile package unavailable: {name}");
                selected = Package.FromOwnedBytes(image);
            }
            Stop();
            var next = new RuntimeSession(selected, saveRoot, Width, Height, DecodeFilename(name), font,
                textEncoding, language, localClock, mythroad, randomState, soundOutput, hostNetwork, uptimeClock,
                trackModuleExecution, cancellation, storage)
            {
                WindowHandles = WindowHandles
            };
            try
            {
                next.milliseconds = milliseconds;
                next.rootPackage = rootPackage;
                next.rootPackageFile = rootPackageFile;
                next.rootPackageFileBytes = rootPackageFileBytes;
                next.workingDirectory = workingDirectory;
                next.WriteRuntimeText(102, oldPackage);
                next.WriteRuntimeText(103, oldScript);
                if (resident != null)
                {
                    uint address = next.Memory.CopyIn(resident);
                    if (name[0] == '*')
                    {
                        next.RegisterPackage(address, (uint)resident.Length, residentIndex);
                    }
                    else
                    {
                        next.Memory.Put32(next.Memory.U32(next.table + 104 * 4), address);
                        next.Memory.Put32(next.Memory.U32(next.table + 105 * 4), (uint)resident.Length);
                    }
                }
                next.Record($"RunFile transition from {packageFile} to {name}");
                next.StartGuest(script, "_dsm", parameter);
                successor = next;
            }
            catch (Exception startupError)
            {
                try
                {
                    next.Dispose();
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("RunFile successor startup and cleanup failed", startupError, cleanupError);
                }
                throw;
            }
        }
    }
}

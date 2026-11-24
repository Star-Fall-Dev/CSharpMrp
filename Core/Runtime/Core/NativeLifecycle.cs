using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private bool stopping;
        private bool hostStopping;
        private bool closingFiles;
        private bool completingExit;
        private uint stopHook;
        private uint eventHook;
        private uint timerHook;
        private uint pauseHook;
        private uint resumeHook;

        private string EncodeStartupText(string value)
        {
            if (value.IndexOf('\0') >= 0)
            {
                throw new RuntimeFault("startup text contains NUL");
            }
            var encoding = (System.Text.Encoding)(textEncoding ?? ScriptChunk.Bytes).Clone();
            encoding.EncoderFallback = System.Text.EncoderFallback.ExceptionFallback;
            try
            {
                return ScriptChunk.Bytes.GetString(encoding.GetBytes(value));
            }
            catch (System.Text.EncoderFallbackException)
            {
                throw new RuntimeFault("startup text is not representable in guest encoding");
            }
        }

        private void WriteRuntimeText(uint slot, string value)
        {
            int end = value.IndexOf('\0');
            int length = Math.Min(127, end < 0 ? value.Length : end);
            Span<byte> buffer = stackalloc byte[127];
            int written = ScriptChunk.Bytes.GetBytes(value.AsSpan(0, length), buffer);
            Memory.WritePadded(Memory.U32(table + slot * 4), 128, buffer.Slice(0, written));
        }

        private void DisarmSession()
        {
            Started = false;
            Memory.ReleaseKeys();
            pendingSmsResults = 0;
            timerDelay = 0;
            Memory.Put32(timerFields + 4, 0);
            Memory.Put32(timerFields + 8, 0);
            NativeText = null;
            if (NativeEdit != null)
            {
                ReleaseNativeEdit(NativeEdit.Handle);
            }
        }

        private void CloseSessionFiles(ref List<Exception>? failures)
        {
            searches.Clear();
            if (files.Count == 0) return;

            var filesToClose = files;
            files = new Dictionary<uint, (System.IO.Stream, string)>();
            closingFiles = true;
            try
            {
                foreach (var file in filesToClose.Values)
                {
                    try
                    {
                        file.Data.Dispose();
                    }
                    catch (Exception error)
                    {
                        (failures ??= new List<Exception>()).Add(error);
                    }
                }
            }
            finally
            {
                closingFiles = false;
            }
        }

        private void RunCleanupStep(Action<RuntimeSession> cleanup, ref List<Exception>? failures)
        {
            try
            {
                cleanup(this);
            }
            catch (Exception error)
            {
                (failures ??= new List<Exception>()).Add(error);
            }
        }

        private static void ThrowCleanupFailures(List<Exception>? failures)
        {
            if (failures == null) return;
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            throw new AggregateException("session cleanup failed", failures);
        }

        private uint StopNative(bool freeMemory)
        {
            if (stopping || Memory.U32(runtimeState) == 0)
            {
                return 1;
            }
            stopping = true;
            List<Exception>? failures = null;
            try
            {
                uint state = Memory.U32(runtimeState);
                if ((state == 1 || state == 2) && ScriptVm.IsFunction(Script.Globals["dealevent"]))
                {
                    RunGuest(() => Script.Call(Script.Globals["dealevent"], 8));
                    Record("native stop exit event delivered with one argument");
                }
            }
            catch (Exception error)
            {
                failures = new List<Exception> { error };
            }
            finally
            {
                try
                {
                    DisarmSession();
                    Memory.Put32(runtimeState, 0);
                    exitRequested = true;
                    if (freeMemory)
                    {
                        Memory.Put32(Memory.U32(table + 91 * 4), 0);
                    }
                    CloseSessionFiles(ref failures);
                }
                finally
                {
                    stopping = false;
                }
            }
            ThrowCleanupFailures(failures);
            return 0;
        }

        private uint InvokeStopHook()
        {
            uint callback = stopHook;
            stopHook = 0;
            return callback == 0 ? 1 : Cpu.Invoke(callback, Array.Empty<uint>());
        }

        private uint InvokeGuestHook(uint callback, ReadOnlySpan<uint> arguments = default) =>
            callback == 0 ? 1 : RunGuest(null, callback, arguments);

        private void CompleteRequestedExit()
        {
            if (completingExit)
            {
                return;
            }
            completingExit = true;
            try
            {
                uint callbackCell = Memory.U32(table + 142 * 4);
                uint callback = Memory.U32(callbackCell);
                uint data = Memory.U32(Memory.U32(table + 143 * 4));
                Memory.Put32(callbackCell, 0);
                if (InvokeStopHook() == 1)
                {
                    StopNative(true);
                }
                if (callback != 0)
                {
                    Record("exit callback invoked after stop");
                    Cpu.Invoke(callback, stackalloc uint[] { data });
                }
            }
            catch (GuestExit)
            {
                Record("reentrant exit terminated without repeating callback");
            }
            finally
            {
                try
                {
                    DisarmSession();
                    Memory.Put32(runtimeState, 0);
                    List<Exception>? failures = null;
                    CloseSessionFiles(ref failures);
                    RunCleanupStep(static session => session.CloseNetwork(), ref failures);
                    ThrowCleanupFailures(failures);
                }
                finally
                {
                    completingExit = false;
                }
            }
        }
    }
}

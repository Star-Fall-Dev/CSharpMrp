using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private readonly Dictionary<uint, uint> platformBuffers = new Dictionary<uint, uint>();
        private int pendingSmsResults;
        private bool pumpingSmsResult;

        private uint AllocateExternalRam(uint size, uint output, uint outputLength)
        {
            Memory.Check(output, 4);
            Memory.Check(outputLength, 4);
            // SDKs can return this block to their native allocator. VM object
            // storage is not part of that heap and must never be lent here.
            uint pointer = Memory.AllocateExternalRam(size);
            if (pointer == 0) return uint.MaxValue;
            Memory.Put32(output, pointer);
            Memory.Put32(outputLength, size);
            Record($"external RAM allocated from native heap: {pointer:x8}+{size}");
            return 0;
        }

        private uint ReleaseExternalRam(uint pointer, uint size)
        {
            if (!Memory.ReleaseExternalRam(pointer, size)) return uint.MaxValue;
            RetireModules(pointer, size);
            Record($"external RAM released: {pointer:x8}+{size}");
            return 0;
        }

        private uint SubmitSimulatedSms(uint flags)
        {
            if ((flags & 16) != 0 && !exitRequested && !disposed)
            {
                if (pendingSmsResults == int.MaxValue)
                {
                    Record("simulated SMS result queue full: MR_FAILED=-1; no SMS sent");
                    return uint.MaxValue;
                }
                pendingSmsResults++;
            }
            Record($"fixed emulated SMS submission: MR_SUCCESS=0, flags={flags}; no SMS sent");
            return 0;
        }

        private void PumpSmsResult()
        {
            if (pendingSmsResults == 0 || !Started || guestClockDepth != 0 || pumpingSmsResult)
            {
                return;
            }
            uint state = Memory.U32(runtimeState);
            if (state == 3 || state == 2 && !TimerRunsWhilePaused)
            {
                return;
            }
            // Consume before entering the guest. Reentrant submissions wait for the next pump.
            pendingSmsResults--;
            pumpingSmsResult = true;
            try
            {
                uint status = InvokeGuestHook(eventHook, stackalloc uint[] { 9u, 0u, 0u });
                if (Started && status == 1 && ScriptVm.IsFunction(Script.Globals["dealevent"]))
                {
                    RunGuest(() => Script.Call(Script.Globals["dealevent"], 9, 0, 0));
                }
                Record("simulated SMS result delivered: MR_SMS_RESULT=9, MR_SUCCESS=0");
            }
            finally
            {
                pumpingSmsResult = false;
            }
        }

        private uint CheckDeviceRecord(uint input)
        {
            Memory.Check(input, 24);
            int ReadIdentifier(uint offset)
            {
                return unchecked((int)(Memory.U8(input + offset) << 24 | Memory.U8(input + offset + 1) << 16 |
                    Memory.U8(input + offset + 2) << 8 | Memory.U8(input + offset + 3)));
            }
            Span<byte> expected = stackalloc byte[16];
            for (int index = 0; index < 7; index++)
            {
                expected[index] = (byte)CompatibilityImei[index + (index < 5 ? 1 : 2)];
            }
            expected[7] = unchecked((byte)(ReadIdentifier(16) % 239));
            expected[8] = unchecked((byte)(ReadIdentifier(20) % 237));
            expected[9] = (byte)CompatibilityModel[0];
            expected[10] = (byte)CompatibilityModel[0];
            expected[11] = (byte)CompatibilityModel.Length;
            expected[12] = 1968 % 251;
            expected[13] = 1968 % 247;
            expected[14] = (byte)(CompatibilityVersion % 253);
            expected[15] = (byte)(CompatibilityVersion % 241);
            for (int direction = 0; direction < 2; direction++)
            {
                byte previous = direction == 0 ? (byte)(CompatibilityImei[0] % 10 * 21 + CompatibilityImei[6]) :
                    (byte)((CompatibilityImei[11] + CompatibilityImei[12]) % 10 * 21 + CompatibilityImei[14]);
                for (int step = 0; step < expected.Length; step++)
                {
                    int position = direction == 0 ? step : expected.Length - step - 1;
                    if (step == 0 || expected[position] != previous)
                    {
                        expected[position] ^= previous;
                    }
                    previous = expected[position];
                }
            }
            for (int index = 0; index < expected.Length; index++)
            {
                if (expected[index] != Memory.U8(input + (uint)index))
                {
                    return uint.MaxValue;
                }
            }
            return 0;
        }

        private void SeedRandom(uint seed)
        {
            randomState = seed == 0 ? 0x6d727031u : seed;
        }

        private uint NextRandom()
        {
            randomState ^= randomState << 13;
            randomState ^= randomState >> 17;
            randomState ^= randomState << 5;
            return randomState;
        }

        private uint PlatformData(uint code, uint input, uint output, uint length)
        {
            byte[] bytes;
            if (code == 1201)
            {
                bytes = FontMetrics(1);
            }
            else if (code == 1017)
            {
                bytes = new byte[] { 3, 5, 5, 1 };
            }
            else if (code == 1116)
            {
                string? buildTime = null;
                foreach (AssemblyMetadataAttribute metadata in typeof(RuntimeSession).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
                {
                    if (metadata.Key == "PlatformBuildTime")
                    {
                        buildTime = metadata.Value;
                        break;
                    }
                }
                if (string.IsNullOrEmpty(buildTime))
                {
                    throw new RuntimeFault("Core assembly is missing PlatformBuildTime metadata");
                }
                string display = DateTime.ParseExact(buildTime, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind).ToString("HH:mm:ss MMM dd yyyy", CultureInfo.InvariantCulture);
                bytes = Encoding.ASCII.GetBytes(display + "\0");
            }
            else if (code == 1305)
            {
                char drive = char.ToLowerInvariant((char)Memory.U8(input));
                if (drive != 'a' && drive != 'b' && drive != 'c')
                {
                    Record("fixed disk query: unknown drive, MR_IGNORE=1");
                    return 1;
                }
                uint total = drive == 'a' ? 1722u : drive == 'b' ? 95u : 1874u;
                uint free = drive == 'a' ? 1271u : drive == 'b' ? 77u : 1873u;
                uint unit = drive == 'c' ? 1048576u : 1024u;
                bytes = new byte[16];
                uint[] values = { total, unit, free, unit };
                for (int index = 0; index < values.Length; index++)
                {
                    for (int part = 0; part < 4; part++)
                    {
                        bytes[index * 4 + part] = (byte)(values[index] >> (part * 8));
                    }
                }
            }
            else
            {
                throw new RuntimeFault($"unknown platform data {code}");
            }
            Memory.Check(output, 4);
            Memory.Check(length, 4);
            if (!platformBuffers.TryGetValue(code, out uint buffer))
            {
                buffer = Memory.CopyIn(bytes);
                platformBuffers.Add(code, buffer);
            }
            else if (code == 1305 || code == 1116)
            {
                Memory.Write(buffer, bytes);
            }
            Memory.Put32(output, buffer);
            Memory.Put32(length, (uint)bytes.Length);
            Record($"fixed platform data {code}: {bytes.Length} bytes; no device query");
            return 0;
        }
    }
}

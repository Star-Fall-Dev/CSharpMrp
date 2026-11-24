namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private static bool ExecuteArmPreload(uint op)
        {
            bool registerOffset = (op & 0x02000000) != 0;
            if ((op & 0xfd700000) == 0xf4100000 && (!registerOffset || (op & 16) == 0))
            {
                return true; // A5-24 unallocated memory hints.
            }
            if ((op & 0xfc30f000) != 0xf410f000)
            {
                return false;
            }
            bool writeIntent = (op & 0x01400000) == 0x01000000;
            if (registerOffset && ((op & 16) != 0 || (op & 15) == 15) ||
                writeIntent && (op >> 16 & 15) == 15)
            {
                throw new RuntimeFault("invalid ARM preload encoding");
            }
            // A3.9.4 permits NOP behavior. A hint must not access guest memory or
            // raise a synchronous fault merely because its address is unmapped.
            return true;
        }

        private static bool IsMemoryBarrier(uint op)
        {
            uint kind = op & 0xfffffff0;
            // Guest loads/stores and instruction fetches complete in program order.
            return kind == 0xf57ff040 || kind == 0xf57ff050 || kind == 0xf57ff060;
        }

        private bool ExecuteArmStatus(uint op)
        {
            if ((op & 0x0fffff00) == 0x0320f000)
            {
                if ((op & 255) == 0x14 && op >> 28 != 14)
                {
                    throw new RuntimeFault("invalid conditional CSDB encoding");
                }
                // A5.2.11: unallocated hints are NOPs. This single-thread,
                // nonspeculative model completes WFE/WFI immediately (B1.8.13/14).
                // SEV has no observable effect without suspended guest processors;
                // YIELD and DBG have no attached hardware scheduler/debug system.
                return true;
            }
            if ((op & 0x0fbf0fff) == 0x010f0000)
            {
                int destination = (int)(op >> 12 & 15);
                if ((op & 0x00400000) != 0 || destination == 15)
                {
                    throw new RuntimeFault("invalid user-mode MRS encoding");
                }
                R[destination] = (N ? 0x80000000u : 0) | (Z ? 0x40000000u : 0) |
                    (C ? 0x20000000u : 0) | (V ? 0x10000000u : 0) | (Q ? 0x08000000u : 0) |
                    (uint)(GE & 15) << 16 | 0x10;
                return true;
            }
            bool immediate = (op & 0x0fb0f000) == 0x0320f000;
            if (immediate || (op & 0x0fb0fff0) == 0x0120f000)
            {
                int mask = (int)(op >> 16 & 15);
                int source = (int)(op & 15);
                if ((op & 0x00400000) != 0 || mask == 0 || (mask & 3) != 0 || (!immediate && source == 15))
                {
                    throw new RuntimeFault("unsupported user-mode MSR encoding");
                }
                uint value = immediate ? Shift(op & 255, 3, (int)(op >> 8 & 15) * 2, false, out _) : R[source];
                if ((mask & 8) != 0)
                {
                    N = (value & 0x80000000) != 0;
                    Z = (value & 0x40000000) != 0;
                    C = (value & 0x20000000) != 0;
                    V = (value & 0x10000000) != 0;
                    Q = (value & 0x08000000) != 0;
                }
                if ((mask & 4) != 0)
                {
                    GE = (byte)(value >> 16 & 15);
                }
                return true;
            }
            return false;
        }
    }
}

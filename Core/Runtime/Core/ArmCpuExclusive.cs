namespace CSharpMrp
{
    public sealed partial class ArmCpu
    {
        private uint exclusiveAddress;
        private uint exclusiveSize;
        private readonly System.Action<uint, uint> exclusiveWriteMonitor;

        private void ClearExclusive()
        {
            if (exclusiveSize != 0)
            {
                exclusiveSize = 0;
                Memory.Written -= exclusiveWriteMonitor;
            }
        }

        private void InvalidateExclusive(uint address, uint size)
        {
            if (exclusiveSize != 0 && size != 0 && address < (ulong)exclusiveAddress + exclusiveSize &&
                exclusiveAddress < (ulong)address + size)
            {
                ClearExclusive();
            }
        }

        private bool ExecuteArmExclusive(uint op)
        {
            int basis = (int)(op >> 16 & 15);
            int destination = (int)(op >> 12 & 15);
            int source = (int)(op & 15);
            if ((op & 0x0fb00ff0) == 0x01000090)
            {
                if (basis == 15 || destination == 15 || source == 15 || basis == destination || basis == source)
                {
                    throw new RuntimeFault("invalid SWP encoding");
                }
                uint location = R[basis];
                bool octet = (op & 0x00400000) != 0;
                if (!octet && (location & 3) != 0)
                {
                    throw new RuntimeFault("unaligned SWP address");
                }
                uint value = octet ? Load8(location) : Load32(location);
                if (octet)
                {
                    Store8(location, R[source]);
                }
                else
                {
                    Store32(location, R[source]);
                }
                R[destination] = value;
                return true;
            }
            bool load = (op & 0x0f900fff) == 0x01900f9f;
            if (!load && (op & 0x0f900ff0) != 0x01800f90)
            {
                return false;
            }
            int width = (int)(op >> 21 & 3);
            uint size = width == 0 ? 4u : width == 1 ? 8u : width == 2 ? 1u : 2u;
            int valueRegister = load ? destination : source;
            if (basis == 15 || destination == 15 || valueRegister == 15 ||
                size == 8 && ((valueRegister & 1) != 0 || valueRegister >= 14) ||
                !load && (destination == basis || destination == valueRegister || size == 8 && destination == valueRegister + 1))
            {
                throw new RuntimeFault("invalid exclusive transfer encoding");
            }
            uint address = R[basis];
            ExclusiveTransfer(address, size, load, valueRegister, destination, valueRegister + 1);
            return true;
        }

        private void ExclusiveTransfer(uint address, uint size, bool load, int valueRegister, int destination, int secondRegister)
        {
            if ((address & (size - 1)) != 0)
            {
                throw new RuntimeFault("unaligned exclusive address");
            }
            CheckData(address, size);
            if (load)
            {
                uint value = size == 1 ? Load8(address) : size == 2 ? Load16(address) : Load32(address);
                if (size == 8)
                {
                    R[secondRegister] = Load32(address + 4);
                }
                R[valueRegister] = value;
                exclusiveAddress = address;
                // Ordinary writes need no delegate dispatch without a reservation.
                // Reuse the handler and do not subscribe again for repeated LDREX.
                if (exclusiveSize == 0)
                {
                    Memory.Written += exclusiveWriteMonitor;
                }
                exclusiveSize = size;
            }
            else
            {
                bool success = exclusiveSize == size && exclusiveAddress == address;
                ClearExclusive();
                if (success)
                {
                    uint value = R[valueRegister];
                    if (size == 1)
                    {
                        Store8(address, value);
                    }
                    else if (size == 2)
                    {
                        Store16(address, value);
                    }
                    else
                    {
                        Store32(address, value);
                        if (size == 8)
                        {
                            Store32(address + 4, R[secondRegister]);
                        }
                    }
                }
                R[destination] = success ? 0u : 1u;
            }
        }
    }
}

namespace CSharpMrp
{
    public sealed partial class GuestMemory
    {
        // Legacy MTK SDKs scan five active-low, 16-bit key registers, four bytes
        // apart. The emulator assigns scan codes 0..20 to the MRP key codes;
        // applications that learn their bindings can use the same host keys.
        internal bool EmulateKeypad { get; set; }
        private uint pressedKeys;

        internal void SetKeyState(int type, int key)
        {
            if ((uint)key > 20) return;
            if (type == 0) pressedKeys |= 1u << key;
            else if (type == 1) pressedKeys &= ~(1u << key);
        }

        internal void ReleaseKeys() => pressedKeys = 0;

        private bool TryReadKeypad(uint address, uint size, out uint value)
        {
            uint offset = address - 0x81080004u;
            uint register = offset / 4, part = offset % 4;
            if (!EmulateKeypad || register >= 5 || part >= 2 ||
                !(size == 1 || size == 2 && part == 0 || size == 4 && part == 0))
            {
                value = 0;
                return false;
            }
            uint bits = register < 2 ? pressedKeys >> (int)(register * 16) : 0;
            value = (~bits & 0xffff) >> (int)(part * 8);
            if (size == 1) value &= 0xff;
            return true;
        }
    }
}

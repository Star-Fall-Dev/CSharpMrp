using System;
using System.Collections.Generic;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private sealed class SdkExpiryPattern
        {
            internal readonly byte[] Bytes;
            private readonly bool[] variable;

            internal SdkExpiryPattern(string hex, params (int Offset, int Length)[] fields)
            {
                Bytes = SdkHex(hex);
                variable = new bool[Bytes.Length];
                foreach (var field in fields)
                    Array.Fill(variable, true, field.Offset, field.Length);
            }

            internal bool Matches(byte[] image, int offset)
            {
                if (offset < 8 || offset > image.Length - Bytes.Length) return false;
                for (int i = 0; i < Bytes.Length; i++)
                    if (!variable[i] && image[offset + i] != Bytes[i]) return false;
                return true;
            }
        }

        // Complete boolean predicates, including the probabilistic expiry period.
        // Only call displacements and validated calendar/threshold constants vary.
        private static readonly SdkExpiryPattern[] sdkExpiryPredicates =
        {
            new SdkExpiryPattern("00b587b001a8fff70ffe15480199814202d2012007b000bd019981421ed10298062801d20120f5e70298062814d10398142801d80120ede7f1f74efdecf7d2e803998b0059188900ff399139814201d20120dfe70020dde70020dbe70020d9e7dc070000",
                (6, 4), (32, 1), (42, 1), (48, 1), (56, 8), (72, 1), (74, 1), (96, 4)),
            new SdkExpiryPattern("00b587b001a8fff729fd15480199814202d2012007b000bd019981421dd10298062801d20120f5e70298062813d10398152801d80120ede7f1f74efaebf774ee03990b01591aff393c39814201d20120e0e70020dee70020dce70020dae70000dc070000",
                (6, 4), (32, 1), (42, 1), (48, 1), (56, 8), (70, 1), (72, 1), (96, 4))
        };
        private static readonly SdkExpiryPattern sdkExpiryDate = new SdkExpiryPattern(
            "10b582b0041c6846fdf710fd00ab1888206098786060d878a0601879e060587920619879606102b010bd", (8, 4));
        private static readonly SdkExpiryPattern sdkExpiryDateImport = new SdkExpiryPattern("034908b57944896b80318968884708bd");
        private static readonly SdkExpiryPattern sdkExpiryTimeImport = new SdkExpiryPattern("034808b57844806b80304068804708bd");
        private static readonly SdkExpiryPattern sdkExpiryRandom = new SdkExpiryPattern(
            "08b50bf003fe0cf0d9fc0cf0cff84142002800db011c081c08bd", (2, 12));
        private static readonly SdkExpiryPattern sdkExpirySeed = new SdkExpiryPattern("0149494408607047");
        private static readonly SdkExpiryPattern sdkExpiryNext = new SdkExpiryPattern(
            "0548064b48440168054a5943891801604800400c70470000000000006d4ec64139300000", (24, 4));
        private static readonly SdkExpiryPattern sdkExpiryRemainder = new SdkExpiryPattern(
            "14309fe5c02fa0e193c0c1e06330e0e3c11262e0930120e01eff2fe11f85eb51");
        private readonly Dictionary<string, int> sdkExpiryMatches = new Dictionary<string, int>(StringComparer.Ordinal);

        private static int SdkExpiryCall(byte[] image, int offset, bool exchange = false)
        {
            if (offset < 8 || offset > image.Length - 4) return -1;
            uint op = SdkWord(image, offset);
            if ((op & 0xf800) != 0xf000 || (op >> 16 & 0xf800) != (exchange ? 0xe800u : 0xf800u))
                return -1;
            int displacement = (int)((op & 0x7ff) << 12 | (op >> 16 & 0x7ff) << 1);
            displacement = displacement << 9 >> 9;
            int target = (exchange ? offset & ~3 : offset) + 4 + displacement;
            return target >= 8 && target < image.Length && (!exchange || (target & 3) == 0) ? target : -1;
        }

        private static bool HasSdkExpiryImport(byte[] image, int offset, SdkExpiryPattern pattern)
        {
            // PC-relative table cell must be the EXT header, followed by precisely
            // slot 34 (calendar) or slot 33 (uptime), not an arbitrary function call.
            return pattern.Matches(image, offset) && offset <= image.Length - 20 &&
                (long)offset + 8 + unchecked((int)SdkWord(image, offset + 16)) + 0x38 == 0;
        }

        private static bool HasSdkExpiryHelpers(byte[] image, int gate)
        {
            int date = SdkExpiryCall(image, gate + 6);
            if (!sdkExpiryDate.Matches(image, date) ||
                !HasSdkExpiryImport(image, SdkExpiryCall(image, date + 8), sdkExpiryDateImport)) return false;
            int random = SdkExpiryCall(image, gate + 56);
            if (!sdkExpiryRandom.Matches(image, random) ||
                !HasSdkExpiryImport(image, SdkExpiryCall(image, random + 2), sdkExpiryTimeImport)) return false;
            int seed = SdkExpiryCall(image, random + 6), next = SdkExpiryCall(image, random + 10);
            if (!sdkExpirySeed.Matches(image, seed) || seed > image.Length - 12 ||
                !sdkExpiryNext.Matches(image, next) ||
                SdkWord(image, seed + 8) != SdkWord(image, next + 24)) return false;
            return sdkExpiryRemainder.Matches(image, SdkExpiryCall(image, gate + 60, true));
        }

        private static int FindSdkExpiryGate(byte[] image)
        {
            int found = -1;
            for (int offset = 8; offset <= image.Length - 100; offset += 4)
            {
                if (SdkWord(image, offset) != 0xb087b500) continue;
                for (int variant = 0; variant < sdkExpiryPredicates.Length; variant++)
                {
                    if (!sdkExpiryPredicates[variant].Matches(image, offset)) continue;
                    uint year = SdkWord(image, offset + 96);
                    int month = image[offset + 32], day = image[offset + 48];
                    int subtract = variant == 0 ? 72 : 70;
                    if (year < 1 || year > 9999 || month < 1 || month > 12 ||
                        image[offset + 42] != month || day < 1 || day > DateTime.DaysInMonth((int)year, month) ||
                        image[offset + subtract] + image[offset + subtract + 2] != day * (variant == 0 ? 20 : 15) ||
                        !HasSdkExpiryHelpers(image, offset)) continue;
                    bool caller = false;
                    for (int call = 8; call <= image.Length - 8; call += 2)
                    {
                        if (image[call + 4] == 1 && image[call + 5] == 0x28 && image[call + 7] == 0xd1 &&
                            SdkExpiryCall(image, call) == offset) { caller = true; break; }
                    }
                    if (!caller) continue;
                    if (found >= 0) return -1;
                    found = offset;
                }
            }
            return found;
        }

        private (uint Address, string? Resource) FindLoadedSdkExpiryGate(uint input, uint length, string? resource)
        {
            IEnumerable<string> candidates;
            try { candidates = resource == null ? rootPackage.Names : new[] { resource }; }
            catch (RuntimeFault) { return (0, null); } // An incomplete index is not a signature match.
            foreach (string name in candidates)
            {
                if (!name.EndsWith(".ext", StringComparison.OrdinalIgnoreCase) || !rootPackage.Contains(name)) continue;
                if (sdkExpiryMatches.TryGetValue(name, out int offset) && offset < 0) continue;
                if (!sdkImages.TryGetValue(name, out byte[]? original))
                {
                    // An unrelated, never-loaded auxiliary EXT may be incomplete.
                    // Its own load still reports that error; signature lookup must not.
                    try { original = rootPackage.Read(name, diagnostic: Record); }
                    catch (RuntimeFault) { sdkExpiryMatches[name] = -1; continue; }
                    sdkImages.Add(name, original);
                }
                if (original.Length != length) continue;
                if (!sdkExpiryMatches.TryGetValue(name, out offset))
                {
                    offset = FindSdkExpiryGate(original);
                    sdkExpiryMatches.Add(name, offset);
                }
                if (offset >= 8 && MatchesOriginalSdkImage(input, length, original))
                    return (input + (uint)offset, name);
            }
            return (0, null);
        }
    }
}

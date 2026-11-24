using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private byte[]? sdkCapacityOriginalCode;
        private uint sdkCapacityCodeLength;
        private uint sdkCapacityPatchOffset;
        private uint sdkCapacityCodeAddress;
        private bool sdkCapacityProfileChecked;

        private void ApplySdkCapacityCompatibility(uint input, uint length)
        {
            if (sdkCapacityCodeAddress != 0)
            {
                if (input == sdkCapacityCodeAddress && length >= sdkCapacityCodeLength &&
                    HasSdkCapacityWindow(Memory, input, sdkCapacityPatchOffset))
                {
                    Memory.Put16(input + sdkCapacityPatchOffset, 0xe001);
                }
                return;
            }
            if (!sdkCapacityProfileChecked && package.Contains("game.ext"))
            {
                sdkCapacityProfileChecked = true;
                byte[] original = package.Read("game.ext", diagnostic: Record);
                byte[] window = SdkHex("2068002805d03868b04207d20020c043e6e7301c");
                int offset = FindSdkWindow(original, window);
                if (offset >= 8 && (offset & 1) == 0)
                {
                    sdkCapacityOriginalCode = original;
                    sdkCapacityCodeLength = (uint)original.Length;
                    sdkCapacityPatchOffset = (uint)offset + 12;
                }
            }
            if (sdkCapacityOriginalCode == null || length != sdkCapacityCodeLength ||
                !TryPatchSdkCapacityCode(Memory, input, length, sdkCapacityOriginalCode, sdkCapacityPatchOffset))
            {
                return;
            }
            sdkCapacityCodeAddress = input;
            sdkCapacityOriginalCode = null;
            Record($"compatibility SDK capacity branch: {input + sdkCapacityPatchOffset:x8}; complete compare/failure/allocation instruction window; original package unchanged");
        }

        internal static bool TryPatchSdkCapacityCode(GuestMemory memory, uint input, uint length,
            byte[] original, uint patchOffset)
        {
            if (length != original.Length || patchOffset < 20 || (ulong)patchOffset + 8 > length)
            {
                return false;
            }
            memory.Check(input, length);
            for (uint index = 8; index < length; index++)
            {
                if (memory.U8(input + index) != original[index])
                {
                    return false;
                }
            }
            if (!HasSdkCapacityWindow(memory, input, patchOffset))
            {
                return false;
            }
            memory.Put16(input + patchOffset, 0xe001);
            return true;
        }

        private static bool HasSdkCapacityWindow(GuestMemory memory, uint input, uint offset)
        {
            return memory.U32(input + offset - 12) == 0x28006820 &&
                memory.U32(input + offset - 8) == 0x6838d005 &&
                memory.U32(input + offset - 4) == 0xd20742b0 &&
                memory.U32(input + offset) == 0x43c02000 &&
                memory.U32(input + offset + 4) == 0x1c30e7e6;
        }

        private sealed class SdkRuntimeRule
        {
            internal readonly string Resource;
            internal readonly int TemplateLength;
            internal readonly (int Offset, byte[] Before, byte[] After, byte[] Window)[] Changes;
            internal readonly byte[] Tail;
            internal readonly int[] TailBranches;
            internal readonly bool ThumbBranches;

            internal SdkRuntimeRule(string resource, int templateLength,
                (int Offset, string Before, string After, string Window)[] changes, string tail,
                int[] tailBranches, bool thumbBranches)
            {
                Resource = resource;
                TemplateLength = templateLength;
                Changes = new (int, byte[], byte[], byte[])[changes.Length];
                for (int index = 0; index < changes.Length; index++)
                {
                    Changes[index] = (changes[index].Offset, SdkHex(changes[index].Before),
                        SdkHex(changes[index].After), SdkHex(changes[index].Window));
                }
                Tail = SdkHex(tail);
                TailBranches = tailBranches;
                ThumbBranches = thumbBranches;
            }
        }

        private static readonly (int Offset, string Before, string After, string Window) SnowFoxGlyphBitTest =
            (196140, "010017e3", "800017e3", "08008de53200000a0410a0e10c009de5f840ffeb0160a0e101408ae0270000ea010017e31500000ab404dde1040050e1120000ca14009de5040050e10f0000dab604dde1");
        private static readonly (int Offset, string Before, string After, string Window) SnowFoxGlyphBitShift =
            (196292, "a770a0e1", "8770a0e1", "0a40a0e1010050e208008de5015085e20400000a0060a0e3018088e2080058e3a770a0e1d7ffffba00009de5010040e200008de5010070e3bdffff1a0000a0e318d08de2");

        // Complete instruction windows, never MRP/resource identities. All redirects are decoded
        // and relocated from their template instruction position into the matched loaded module.
        private static readonly SdkRuntimeRule[] sdkRuntimeRules =
        {
            // Snow Fox's status-menu resume callback redraws the HUD and menu over a retained map.
            // Match all three windows together so the black-fill change and the glyph fixes apply to the original image in one pass.
            new SdkRuntimeRule("cfunction.ext", 251184, new[]
            {
                SnowFoxGlyphBitTest,
                SnowFoxGlyphBitShift,
                (0x32e68, "8df3ffeb", "0000a0e1",
                    "0300000a010050e30800d405030050030300001a0020a0e30010a0e30000a0e38df3ffeb0000a0e31080bde8ac000000ff4d2de918d04de254409de548708de2"),
            }, "", Array.Empty<int>(), false),
            // The clipped glyph fix also applies when the resume callback differs.
            // UC2 and IGF rows are MSB first, as used by the normal text renderer.
            new SdkRuntimeRule("cfunction.ext", 251184, new[] { SnowFoxGlyphBitTest, SnowFoxGlyphBitShift }, "", Array.Empty<int>(), false),
            // The integer bytecode VM already maps zero-divisor division to zero.
            // Give remainder the same conversion, reusing that exact cleanup path.
            // Reuse the popped stack index to make room for the check in place;
            // the unchanged division window also anchors the branch destination.
            new SdkRuntimeRule("skf_plugin.ext", 19200, new[] { (9956, "3069002801d13861", "3069002801d13861", "49688847002006900de46089411e6181e16880000e5806966189e068890047583069002801d1386103e03969fdf79eec3861b088013800040014b08003d13068"), (10014, "6189e0688900475839693069", "04380f5830690028e0d03969", "b08003d130684168301c8847002006901ae46089411e6181e16880000e5806966189e0688900475839693069fdf782ec3961b088013800040014b08003d13068") }, "", Array.Empty<int>(), true),
            // The cache is the RGB565 screen surface, so each decoded output pixel
            // occupies two bytes even when the movie leaves its pixel mode at zero.
            // Change only the capacity operand; retain the movie parser and cache.
            new SdkRuntimeRule("flaengine.ext", 129312, new[] { (65034, "9279", "0222", "8cfc099004e0281c0a990bf03af90990a06802238019c15e0423c25e51430b9a9279514301610398002815d15548484400684068814207dd1022142101980cf0") }, "", Array.Empty<int>(), false),
            // ExRamInit assumes a base heap smaller than its 800 KiB target. With a larger
            // heap, reserve a real pool up to that target from the reported free bytes.
            // Keep the SDK allocation/failure path; never report an unallocated pool.
            new SdkRuntimeRule("game.ext", 121732, new[] { (67736, "401a4d44", "0df074f9", "4a441032111f0091019200220439081ff0f714fb19216068c903081aa168154d401a4d441835002860611addeff7caff002804d06069f0f7bff900280ad10021") }, "401a04dc1920c003814200d2081c4d447047", Array.Empty<int>(), true),
            // A missing offline URL cannot start a request. Return failure before parsing it.
            new SdkRuntimeRule("cfunction.ext", 182500, new[] { (22964, "f0412de9", "ca9b00ea", "14109fe50000a0e3091081e0001091e5540881e50880bde81c04000018040000f0412de9c4df4de20080a0e10000a0e30c038de508038de5826f8de20140a0e1") }, "000051e30000e0031eff2f01f0412de92f64ffea", new[] { 16 }, false),
            // A full 22-byte MRP title has no terminator. Terminate it in the list's
            // label buffer before formatting the ordinal and copying the label back.
            // At the existing 300-entry limit, close the search before allocating
            // another row. The next filename pointer would overwrite the first label pointer.
            new SdkRuntimeRule("cfunction.ext", 76556, new[]
            {
                (56748, "003197e7", "561300ea", "001197e70400a0e11620a0e3b6f9ffeb0400a0e14ff5ffeb000095e5c0109fe5003197e7012080e201108fe000c09ae50800a0e13cff2fe1000095e50810a0e1000197e7"),
                (56812, "7f00a0e3", "4a1300ea", "000197e79c209fe5092082e0002092e532ff2fe1000095e5010080e2000085e57f00a0e371f9ffeb001095e501018be71800a0e36df9ffeb001095e5010187e7000095e5")
            }, "003197e70020a0e31620c3e5a4ecffea4b0f50e3c4ecffaa7f00a0e3b0ecffea", new[] { 12, 20, 28 }, false),
            // The dialog callback may complete without a selection record. Leave the current form intact.
            new SdkRuntimeRule("game.ext", 86424, new[] { (64848, "70402de9", "101500ea", "c810a0e3edfcffeb080094e508d08de21040bde810ff2fe1fca3ffffb405000070402de910d04de2000050e310d08d120160a0e17080bd180000a0e3b0409fe5") }, "000051e31eff2f0170402de9eaeaffea", new[] { 12 }, false),
            // An empty progress range draws its background, with no division or filled segment.
            new SdkRuntimeRule("cfunction.ext", 61604, new[] { (18608, "950601e0", "fb2900ea", "0638a0e1040055e10450a0c14338a0e10280a0e14118a0e100a0a0e1b70a00eb950601e00400a0e10ceeffeb0058a0e14558a0e10400a0e19b0601e007eeffeb"), (2452, "440190e5", "c63900ea", "38402de9a4409fe5000051e3094084e01c00001a4c0094e5481190e54c2190e5440190e5021081e0014190e74b0fa0e3ee1e00eb78109fe50050a0e1091081e0") }, "000054e314d6ff0a950601e0ffd5ffea503190e502c081e003005ce13880bd28440190e532c6ffea", new[] { 4, 12, 36 }, false),
            // BMP rows are bottom-up: row height - 1 - y, never height - y.
            new SdkRuntimeRule("game.ext", 25108, new[] { (14528, "121b5a43", "02f0a8fc", "0b18009300ab588a0024002838dd00ab198a07985a8a614349004118e86b009b121b5a438018102e08d100ab1a8a1d4b4b441b685200fcf7a4fb1ce0182e1ad1") }, "121b013a5a437047", Array.Empty<int>(), true),
            // These scroll routines pass a literal zero to memcpy for the first scratch array.
            // Use the adjacent memset import only at those calls; preserve their actual memcpy calls.
            new SdkRuntimeRule("game.ext", 134572, new[] { (87396, "2368", "e36a", "f021081a400400140590281c384327d1ea484844407c002820d1ea4ce8484c442368782200214844984778220021e64be3484b441b6848447838984702990198"), (87510, "1b68", "db6a", "7942090409141591ff210231c91b01390e0436141496002871d1cd4bcc484b441b68782200214844984778220021c94bc7484b441b68484478389847002d01db") }, "", Array.Empty<int>(), false),
            new SdkRuntimeRule("game.ext", 182088, new[] { (130952, "2368", "e36a", "f021081a400400140490281c384328d1ea4848444078002821d1ea4ce8484c442368c322002148449847c3220021e64be3484b441b684844c338984700a903c9"), (131068, "1b68", "db6a", "7942090409141591ff211131c91b01390e0436141496002871d1cd4bcb484b441b68c322002148449847c3220021c94bc6484b441b684844c3389847002d01db") }, "", Array.Empty<int>(), false),
            new SdkRuntimeRule("game.ext", 115016, new[] { (70458, "2368", "e36a", "f021081a400400140490281c384328d1e9484844807c002821d1e84ce7484c442368782200214844984778220021e44be2484b441b6848447838984700a903c9"), (70574, "1b68", "db6a", "7942090409141591ff211131c91b01390e0436141496002871d1cb4bca484b441b68782200214844984778220021c74bc5484b441b68484478389847002d01db") }, "", Array.Empty<int>(), false),
            new SdkRuntimeRule("game.ext", 173964, new[] { (123318, "2b68", "eb6a", "281cdc4cff227732384300284c4425d1da484844407800281ed1d84d00214d442b68a0189847ff2277320021d54b201c4b441b68984700a903c9603948430599"), (123428, "1b68", "db6a", "7942090409141191ff211131c91b01390e0436141096002872d1bd4bbe484b441b68002148449847ff2277320021b94b201c4b441b689847002d01db002405e0") }, "", Array.Empty<int>(), false),
            // An empty root suffix has no trailing separator to remove.
            new SdkRuntimeRule("game.ext", 142960, new[] { (13020, "421e291c", "1ff0c8fd", "00200021002200230fc70fc7b148bd4948440068494443600968203f281c8847421e291c381cb84b4b441b6801e054e20de298470023321cb4497944381cfef785fdfef7") }, "002802d0421e291c70470022291c7047", Array.Empty<int>(), true),
            // This launcher confirms the child entry point and then exits before it can
            // queue the restart. Let the restart dispose of the old app during the handoff.
            new SdkRuntimeRule("cfunction.ext", 12548, new[]
            {
                (0x2a76, "fff71ff8", "c046c046", "0122291c0c36301cfff7fdfc002807d10122291c0c36301cfff7f5fc0028e4d0fff71ff82068a268311cfff75afddce7000004000000e2010000ca01000070b5"),
            }, "", Array.Empty<int>(), true),
            // Selecting a file from an empty vector must leave the menu unchanged.
            new SdkRuntimeRule("game.ext", 63784, new[] { (33676, "001091e5", "651d00ea", "01108fe0002094e532ff2fe1000050e30300a003f041bd083808000af081bde8001091e5000191e72c129fe501108fe032ff2fe1000050e3f081bd08040095e5281095e5") }, "04c091e500005ce3f081bd08001091e594e2ffea", new[] { 16 }, false),
            // Optional sprite images can have an empty name. A failed lookup returns -1;
            // the resource getter must return null instead of indexing before its array.
            new SdkRuntimeRule("game.ext", 146020, new[] { (81534, "0e1c051c", "0ff0f1fd", "0a9a0190009205911099119a099b002004970396f1f7f1f9a0610bb0f0bdf0b50e1c051c8b005918406889004418e06883b0002846d1254a7a44291c103101af381cf3f7") }, "002901da0020f0bd0e1c051c7047", Array.Empty<int>(), true),
            // Root modules have no parent EXT descriptor. Their sendAppEvent wrapper
            // returns MR_IGNORE when there is no parent, preserving real callbacks.
            // Their chat navigation also returns immediately for an empty list,
            // before the selection modulo operation (which requires a nonzero count).
            new SdkRuntimeRule("cfunction.ext", 124600, new[] { (39392, "28c093e5", "345300ea", "e42500000c0500002cc09fe508402de90cc08fe004c01ce500308de50c309ce528c093e50230a0e10120a0e10010a0e10100a0e33cff2fe10880bde83066ffff28109fe5"), (14324, "0c0054e3", "b46b00ea", "096086e00020d6e50080a0e30350a0e1000051e309b08be0097087e02a00001a0c0054e31400000a0d0054e3f08dbd180240a0e1011082e297f2ffeb0010c6e50180c6e5") }, "000053e30100a0030880bd0828c093e5c5acffea000050e3f08dbd080c0054e34694ffea", new[] { 16, 32 }, false),
            new SdkRuntimeRule("cfunction.ext", 127040, new[] { (45432, "28c093e5", "b04f00ea", "fe85bde8183600002cc09fe508402de90cc08fe004c01ce500308de50c309ce528c093e50230a0e10120a0e10010a0e10100a0e33cff2fe10880bde8984effff28109fe5"), (27304, "0c0054e3", "696100ea", "096086e00020d6e50080a0e30350a0e1000051e309b08be0097087e02a00001a0c0054e31400000a0d0054e3f08dbd180240a0e1011082e21de6ffeb0010c6e50180c6e5") }, "000053e30100a0030880bd0828c093e549b0ffea000050e3f08dbd080c0054e3919effea", new[] { 16, 32 }, false),
            new SdkRuntimeRule("game.ext", 107980, new[] { (31444, "19", "f7", "09b030bd07f086f81c4a231c4a441032111f0091019200220439081ff9f722f819216868c903081aa968401ae968401a2969411a15487844154a69614a44126890") }, "", Array.Empty<int>(), false),
            new SdkRuntimeRule("cfunction.ext", 154656, new[] { (124256, "3cff2fe1", "ae1d00ea", "0880bde888119ce580e080e00e0181e0042090e5080090e50e1191e7e4c09ce53cff2fe10000a0e3f4ffffeae81afeff9c130000584c0000fc412de9e8709fe50280a0e1") }, "000051e33cff2f114de2ffea", new[] { 8 }, false),
            new SdkRuntimeRule("game.ext", 343412, new[] { (3284, "00284c44", "53f04ef8", "02b010bd0020fbe701484844704700007c35000070b5134b134c4b441d6882b000284c4406d1131c0a1c10497944201ca84713e00e4e7e44002908d00091311c0192321c") }, "4c44002806d1002904d100202070201c02b070bd00287047", Array.Empty<int>(), true),
            new SdkRuntimeRule("game.ext", 346364, new[] { (3284, "00284c44", "53f012fe", "02b010bd0020fbe701484844704700008435000070b5134b134c4b441d6882b000284c4406d1131c0a1c10497944201ca84713e00e4e7e44002908d00091311c0192321c") }, "4c44002806d1002904d100202070201c02b070bd00287047", Array.Empty<int>(), true),
            new SdkRuntimeRule("game.ext", 40916, new[] { (2664, "5949061c", "09f0b4fa", "05a80021002206c05d4b04c04b441b680c380c2298475b4d4d44a869fff712fe5949061c7944574c584f4c444f443a687434201c904731683a68201c9047201c02f044ff") }, "002801d109b0f0bd0149061c7047c0469c4f0000", Array.Empty<int>(), true),
            new SdkRuntimeRule("game.ext", 54068, new[] { (18332, "044080e2", "e42200ea", "140054e3110054134100001a080097e5000050e30c00001a000097e57bfeffeb044080e2000097e578feffeb041080e2003098e50b00a0e10420a0e133ff2fe10000a0e3"), (18384, "004090e5", "db2200ea", "0b00a0e10420a0e133ff2fe10000a0e32bf7ffeb310000ea040097e56efeffeb004090e5002098e50a00a0e10410a0e132ff2fe10400a0e171ffffeb270000ea030055e3"), (18496, "044080e2", "c32200ea", "c000a0e1040050e11b0000da080097e5000050e30c00001a000097e552feffeb044080e2000097e54ffeffeb041080e2003098e50b00a0e10420a0e133ff2fe10000a0e3"), (18548, "004090e5", "ba2200ea", "0b00a0e10420a0e133ff2fe10000a0e302f7ffeb080000ea040097e545feffeb004090e5002098e50a00a0e10410a0e132ff2fe10400a0e148ffffeb5ff6ffeb0000a0e3") }, "000050e354ddff0a044080e216ddffea000050e350ddff0a004090e51fddffea000050e34cddff0a044080e237ddffea000050e348ddff0a004090e540ddffea", new[] { 4, 12, 20, 28, 36, 44, 52, 60 }, false),
            new SdkRuntimeRule("game.ext", 153136, new[] { (89296, "01d1", "c046", "fdf796f86070002801d102b010bd0120207008487844fbf74bfa00f0a5f8012801d100f02bf80020efe70000c8000000eb51000039cfffff10b5ff21cd3101607d21") }, "", Array.Empty<int>(), false),
            new SdkRuntimeRule("cfunction.ext", 54824, new[] { (14264, "950601e0", "9a2700ea", "0638a0e1040055e10450a0c14338a0e10280a0e14118a0e100a0a0e1ea0700eb950601e00400a0e15cf2ffeb0058a0e14558a0e10400a0e19b0601e057f2ffebf030a0e3") }, "000054e375d8ff0a950601e060d8ffea", new[] { 4, 12 }, false),
            new SdkRuntimeRule("mrplist.ext", 30244, new[] { (13676, "fcf786ed", "04f05af8", "16d00133994235d0013399423bd00133994271d1002020756969606841432068fcf786ed2061082866da0820206163e01c2002f0bdf8041c0bd15f495f4b79444b441b68") }, "002801d10820704700b5f8f726ed00bd", new[] { 10 }, true),
            new SdkRuntimeRule("rx.ext", 155972, new[] { (12770, "421e291c", "22f0afff", "00200021002200230fc70fc7b548be4948440068494443600968203f281c8847421e291c381cb94b4b441b68984702e02be23ce20ce20023b649321c7944381cfef703fe") }, "002802d0421e291c70470022291c7047", Array.Empty<int>(), true),
            new SdkRuntimeRule("game.ext", 41544, new[] { (10346, "fdf7dafb", "07f0edfc", "02f003f900206870206800f080fe0020c0432060032060710022311c201ce368fdf7dafb012070bc08bc18470020fae70000b805000070b50a4e051c00244e44e0010019") }, "002b00d018477047", Array.Empty<int>(), true),
            new SdkRuntimeRule("cfunction.ext", 202088, new[] { (86192, "212000eb", "2c7100ea", "34109fe5091081e0001091e51040bde811ff2fe1040090e5332000eb000094e5212000eb001094e50000a0e3000081e5000084e51080bde8405c01002400000004009fe5"), (86204, "000081e5", "0000a0e1", "1040bde811ff2fe1040090e5332000eb000094e5212000eb001094e50000a0e3000081e5000084e51080bde8405c01002400000004009fe5090080e0e2ffffead41e0000") }, "0020a0e3002080e5f1aeffebce8effea", new[] { 8, 12 }, false),
            new SdkRuntimeRule("cfunction.ext", 23492, new[] { (12094, "28188642", "02f041fe", "1c68a4181c60cb689b1acb608b68c31a9a184b689a42bbdd4a60b9e7031c006828188642d4d8086848600020b0e770020000234af0b44a441368021c0731cc08e4003cd0"), (11938, "70b4", "70b5", "dae71c030000f802000068260000000300005401000037240000092400002a4970b449440968002902d1002070bc70470730c208c868d200904203d8086848600020"), (11950, "70bc", "70bd", "0000000300005401000037240000092400002a4970b449440968002902d1002070bc70470730c208c868d200904203d8086848600020f3e7002a01d10020efe78869") }, "2818984201d8002070bd8642fdf7b7f9", new[] { 12 }, true),
            new SdkRuntimeRule("game.ext", 133840, new[] { (109364, "0c1c171c", "05f0ccff", "002903db40190321fff7f6fc0134062cf2db38bc08bc18477c7c0000f0b5051c0c1c171c85b0fcf727ff002801d105b0f0bdfdf7d7fe061c002d43d1012c41d154484844") }, "054ca04203d1002a01d10020f0bd0c1c171c7047c046c04689130000", Array.Empty<int>(), true),
        };
        private Package? originalCompatibilityPackage;
        private readonly Dictionary<string, byte[]> sdkImages = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly Dictionary<SdkRuntimeRule, int> sdkRuleMatches = new Dictionary<SdkRuntimeRule, int>();
        private readonly Dictionary<(uint Input, SdkRuntimeRule Rule, int Delta), uint> sdkRuntimeTails =
            new Dictionary<(uint, SdkRuntimeRule, int), uint>();

        private static byte[]? missingStartupSimple;
        private static byte[]? missingStartupLoader;

        internal static bool RequiresDefaultScreen(Package archive, int width, int height)
        {
            if (width == 240 && height == 320 || !archive.Contains("game.ext"))
            {
                return false;
            }
            byte[] image = archive.Read("game.ext");
            byte[] selector = SdkHex("10b584b001a80021002206c004c00c3802f0aefa254c01990220f0294c4418d1029a0821ff3a413a002a07d10120e0621f487844a0626180218017e0029aff3a913a13d1e0621b487844a062618021800ce00199b02909d10299dc2906d1e06215487844a062042060802080");
            int found = -1;
            for (int offset = 8; offset <= image.Length - selector.Length; offset += 2)
            {
                bool same = true;
                for (int index = 0; index < selector.Length; index++)
                {
                    // BL displacement, PC-relative literals and compared dimensions
                    // may move with the SDK; register/branch/control flow must agree.
                    bool relocated = index >= 16 && index < 20 ||
                        (index & 1) == 0 && (selector[index + 1] & 0xf8) == 0x48 ||
                        index == 26 || index == 36 || index == 38 || index == 62 ||
                        index == 64 || index == 84 || index == 90;
                    if (!relocated && image[offset + index] != selector[index])
                    {
                        same = false;
                        break;
                    }
                }
                if (!same)
                {
                    continue;
                }
                uint branch = SdkWord(image, offset + 16);
                if ((branch & 0xf800) != 0xf000 || (branch >> 16 & 0xf800) != 0xf800)
                {
                    continue;
                }
                int displacement = (int)((branch & 0x7ff) << 12 | (branch >> 16 & 0x7ff) << 1);
                displacement = displacement << 9 >> 9;
                long target = offset + 20L + displacement;
                byte[] wrapper = SdkHex("08b57944896bff3141310968884708bd");
                if (target < 8 || target > image.Length - 20 || image[(int)target + 1] != 0x49 ||
                    !image.AsSpan((int)target + 2, wrapper.Length).SequenceEqual(wrapper))
                {
                    continue;
                }
                if (found != -1)
                {
                    return false;
                }
                found = offset;
            }
            if (found < 0)
            {
                return false;
            }
            int firstWidth = image[found + 26];
            int firstHeight = image[found + 36] + image[found + 38];
            int secondHeight = image[found + 62] + image[found + 64];
            int thirdWidth = image[found + 84];
            int thirdHeight = image[found + 90];
            bool supportsDefault = firstWidth == 240 && (firstHeight == 320 || secondHeight == 320) ||
                thirdWidth == 240 && thirdHeight == 320;
            bool supportsRequest = width == firstWidth && (height == firstHeight || height == secondHeight) ||
                width == thirdWidth && height == thirdHeight;
            return supportsDefault && !supportsRequest;
        }

        private ScriptPrototype LoadSdkStartup(byte[] bytes, string name)
        {
            if (HasNativeModuleHeader(bytes))
            {
                Record($"compatibility native startup: {name} uses the SDK initialization and event wrapper");
                var wrapper = ScriptChunk.Load(SdkStartup(false), name);
                if (name != "cfunction.ext") SetSdkStartupModule(wrapper, name);
                return wrapper;
            }
            if (name == "start.mr" && !package.Contains("mrc_loader.ext") && package.Contains("cfunction.ext") &&
                bytes.AsSpan().SequenceEqual(SdkStartup(true)) && IsDirectSdkLoader(package.Read("cfunction.ext")))
            {
                Record("compatibility startup: complete standard wrapper references an absent mrc_loader.ext; initialize the included cfunction.ext directly");
                return ScriptChunk.Load(SdkStartup(false), name);
            }
            ScriptPrototype script = ScriptChunk.Load(bytes, name);
            if (name != "start.mr" || !ReferenceEquals(package, rootPackage) || script.Parameters != 0 ||
                script.Upvalues != 0 || script.Code.Length <= 108 || script.Constants.Length <= 34 ||
                script.Code[22] != 0x00801514 || script.Code[23] != 0x00000385 || script.Code[108] != 0x000000c5 ||
                ScriptVm.Text(script.Constants[3]) != "_mr_c_load" || ScriptVm.Text(script.Constants[16]) != "sdk_key.dat" ||
                ScriptVm.Text(script.Constants[34]) != "cann`t find sdk key!")
            {
                return script;
            }
            script.Code[23] = 20u | (131071u + 84u) << 6;
            Record("compatibility SDK key startup: parsed guard branches to the existing _mr_c_load entry; SDK initialization and original bytes retained");
            return script;
        }

        private static void SetSdkStartupModule(ScriptPrototype wrapper, string name)
        {
            for (int i = 0; i < wrapper.Constants.Length; i++)
                if (wrapper.Constants[i] is string value && value == "cfunction.ext") wrapper.Constants[i] = name;
            foreach (var child in wrapper.Children) SetSdkStartupModule(child, name);
        }

        private const string missingStartupSimplePacked =
            "H4sIAAAAAAAAC51Wz28bRRR+82PXTqCFloAQ4VB6qhCEtOoFCdqaxI16AAoJkCChxVlv0m3ttbW7ttoD0vqQVpzbiw9cQCAQCP6GHEIvHOHAlX8E" +
            "vjczm2xcYxFW+nbmvZ1575s37z178d0PbxZinoiuZXkrzZe6Kbln7m285AQuAxeAtxw+B1aBCNgGbgO7QA/oAKMKvprAN/8B3zp8NwXfV/DDBH48" +
            "AX4Cfnb4xZ3zKaAfdNOg30pbXVqC+CbAgTKaoBMlh1p+XgK0h1cQ9rqkXj9NpFqwotkSGwqDTq/VJj3HEQqDPO5GGSneqtlqO2p1omGU5EciL0lJ" +
            "1yFmg6wfJdhdg5BG2aAbuQ/3sjjZ6Vk3a1G+fi+7YWT+GGR5usJsXoEfJdiVj9ewO4RdxVOtedluSFocMjk6tfEGG3Gya8+WDbY/trN+K7xjd8dx" +
            "TOrL59zpm3fjnDwXkwt20Gfxem0Klip4YwqWHS45cPKZU5gg5HQK42nnq16lHwbbg52JGGzhWjWvDncGSZjHvWQpupuTOsexYQsmQOpvPCwx56ZN" +
            "XLGPnBgXgjgcfLYXgUeiMN+FIAGdbAhSY6TOIhhuUEH7sMbzxcLyWzWRoDNXnN0S1yewVsGNCt6fgpsVfFDB+gQ+mcDmCfDpCfHZFARkG8XTLn5h" +
            "rx2ZoHCH4dj3l4+LF4+Llw5FbRYv0/MYXy3li8vmRljm4uqv9JKcC+llpzQuuV6riv57gy6dqygSXlEquAUMTYMrvXK+lTI/Z1h/vJYXWMU5txFl" +
            "ucm5h6KaW5qnQW7LBmlJWhpT/1JmvKodZNV645KN+TkqvScLnZ8r9lD6hdEXJlP3MEcWe8hWf6sQGiN71Y+Qqbzut6vSyHvQs2pMQn90TdKCXxBk" +
            "LSR560LCqfAw9x+OpNfE6De4jcjanyR8yHW/AT6FnINchzx/v+HNN5Tpprh8dcqUYCGeoUI9Oy7U/GOs+V2ikiTp+w2pR8rchb9ZUG0BjsGztglb" +
            "bl4fF1Jjjz5Q4KTJOwAHVB78S41q88pq27aDaQjhDLQdIjp6DvtiHgxtyPOAb//JSNumqaTbyM4QaNrnriBsSwAxLmXBV8zlXm0JuxWSt2Ygdrg9" +
            "jWRWksxmk9T/k2SnQrI7A4lDbxrJtCSZziZZ/nCchCQXJO9r4BuatUR603nIj1kHszVh0+EBxq9Zj/EvjDVc23nM1zAijWgMWw94Tnb+x1U0+Tpq" +
            "AfSQwviPJCTSU+0h/a6/IzWamkQKqi3wauKQW9hTrh1py5m/HWj7g7GhYQtEf10h1gmUCK+XWK+g1/6q1PseStQ3cash4026c0nAjuQflaZv/fDl" +
            "/APtE0ZGugkAAA==";
        private const string missingStartupLoaderPacked =
            "H4sIAAAAAAAAC51WzW8bRRR/87Hrj5a0VdOqEA6lICEhESXckEobkzhRDkAhARIktHXsbXDxl3bXUXtAWh/SinNz8YELCAQCwZ+AfAi9cGwPufKP" +
            "wO/NjJ2Na1zCSj/PezNv3rx5X+O59z66lYoiES3FSSVK5psRua/wDn7kGK4AHnDd4TawAoTADnAX2AXaQAPoZfDNGL77D/je4YcJ+DGDn8bw8ynw" +
            "C/Crw2/unmeATtCMgk4lqjRpHuzbADvKzASNsDWa5e8lQLNngmq7SerNGSJVgRbNmlhRNWi0KzXSBfZQNUjqzTAmxVs1a62FlUa4F7aSY5ZFItJ5" +
            "sHE37oQt7M6BicK42wzdwv243rrTtseshcnG/Xjd8LwYxEm0zNa8gnOU4KN8/Ow196BXMak1i+1WSYuRJce3NqdBR721a+8Wd3c+sVSnUv3S7q7X" +
            "66S+vuhuX75XT0yCkEsUdhr7yZ+AXAb5CSg4FCfgzAScdXjhOZhxOO9wAbjoMAtcdrji7NTG6YnZ+4ZLkD0j+XpmNctXgw69eIJvjHjPyi/SqyBe" +
            "c66ayXq/Gux074yFcBuH6nOYaUY2k8JoPryXkLrKsWUVin9NqG0wbcCzIWS627KhY+H1dVJ/42NpjlGZTKGKAa7YT4WJi+f8cJBPzboQJDAnS4JU" +
            "H564tASf+WZNDARJIUmVJOkjkPugVwdCCQUnpOQdpUI9/V2ITWnkPejyiyRylArcVRYCpAL05qHXm8MxT0RKJUWGnrW0AC02ydAEmuZS68AVk790" +
            "4Ya7xxCrY1jLYD2DDybgVgYfZrAxhk/HsHUKfHZKfD4B8JtpxGddvKrtWmicwh2cA91ZOMkunmTfGrHaCC/QJbKZbvjFBZMBzHM1dpbbrYQb1ctu" +
            "0hzJ/TA70Xm/26SrmYkWSwwnbAVdz5zKFTTk+eOaHOuVXGCmKDbDODFF8UgMc9/kOpNBYtsScp60NKr+pY2xVC2Is/2Mi73O33Fre7aR8nfDXkpf" +
            "7n1FB8jMfdADl9HbqdAY+VR9wGmP8c+b0vD7wlZCn4T+eEmaygGvUTXehpA4VHig/Uc96ZUx+iVu0zJ3RMIHn/dLsCeVBfB58MUHJa+ISnDNT6FB" +
            "6RlU0zlK1fl+qoqPIfOEqw0V+aAkdU+ZWPhbKeVmcTDszG1Bl6Pz/VRq7NGHqFihyTuEDahInC81V+Gw2nbsYDpWdQpqDiEdf6N3Jwn2rMuTgKP/" +
            "rKddH5NuIx8GR9OAu5CwLQiGcSkLDjGXe7Yl7GaM/GIK6g53JxkZD42Mpxup/6eRjYyRzSloObQnGRkNjYymGzl8mE9jJBck7ythDY+DRHrTNfCP" +
            "eQ5qc8Kmw0OM3/I8xr8w5hC2a6DXMCKNqA9dD5kmSz+9aR8VD+ZxY8dDIJGeCo+GXn1XajQ1iRRU27CrjEtuY89Qtqetzbx2qO0DtamhC4b+sUw8" +
            "J1AiLC8hrzCv/RWpBx5K1Hf/PVKb7lwS0CP5USn79hwOzj+oqDQQGgsAAA==";

        private byte[]? ReadMissingSdkStartup(string name)
        {
            if (name != "start.mr" || package.Contains(name) || !ReferenceEquals(package, rootPackage) ||
                !package.Contains("cfunction.ext") || !package.Contains("game.ext"))
            {
                return null;
            }
            byte[] code = package.Read("cfunction.ext", diagnostic: Record);
            if (!IsDirectSdkLoader(code)) return null;
            Record("compatibility startup: missing script with validated MRPG/CMAP loader ABI; original package remains unchanged");
            return SdkStartup(package.Contains("mrc_loader.ext"));
        }

        private static bool HasNativeModuleHeader(byte[] code) => code.Length >= 32 &&
            SdkWord(code, 0) == 0x4750524d && SdkWord(code, 4) == 0x50414d43;

        private static bool IsDirectSdkLoader(byte[] code)
        {
            if (!HasNativeModuleHeader(code) || SdkWord(code, 8) != 0xe92d4010 || SdkWord(code, 12) != 0xe1a04000 ||
                SdkWord(code, 16) != 0xe1a00004 || (SdkWord(code, 20) & 0xfe000000) != 0xfa000000 ||
                SdkWord(code, 24) != 0xe8bd8010)
            {
                return false;
            }
            int delta = (int)(SdkWord(code, 20) << 8) >> 6;
            long target = 28L + delta + ((SdkWord(code, 20) >> 23) & 2);
            return target >= 28 && target < code.Length && (target & 1) == 0;
        }

        private static byte[] SdkStartup(bool loader)
        {
            if (loader)
            {
                missingStartupLoader ??= Package.ReadPackedBytes(Convert.FromBase64String(missingStartupLoaderPacked));
            }
            else
            {
                missingStartupSimple ??= Package.ReadPackedBytes(Convert.FromBase64String(missingStartupSimplePacked));
            }
            return loader ? missingStartupLoader! : missingStartupSimple!;
        }

        private bool? installerSleepSupported;

        private bool AdvanceInstallerSleep(uint duration)
        {
            // Installation bundles carry split font parts or plugin binaries, rather than a
            // package identity. Their clock-polling waits advance virtual time on the owner.
            if (!installerSleepSupported.HasValue)
            {
                installerSleepSupported = rootPackage.Names.Any(name => name.Contains(".part")) && rootPackage.Contains("mrc_loader.ext") ||
                    rootPackage.Contains("cfunction.ext") && rootPackage.Names.Count(name => name.EndsWith(".bin", StringComparison.Ordinal)) >= 2;
            }
            if (!installerSleepSupported.Value)
            {
                return false;
            }
            if (duration > 60000)
            {
                throw new RuntimeFault("installer sleep exceeds 60000 ms");
            }
            ReadRuntimeClock();
            milliseconds = unchecked(milliseconds + duration);
            guestClockBaseline = milliseconds;
            guestClockTimestamp = uptimeClock();
            return true;
        }

        private static int FindSdkWindow(byte[] image, byte[] window)
        {
            if (window.Length < 16)
            {
                return -1;
            }
            int found = -1;
            for (int offset = 8; offset <= image.Length - window.Length; offset += 2)
            {
                if (image[offset] != window[0])
                {
                    continue;
                }
                int index = 1;
                while (index < window.Length && image[offset + index] == window[index])
                {
                    index++;
                }
                if (index == window.Length)
                {
                    if (found != -1)
                    {
                        return -1;
                    }
                    found = offset;
                }
            }
            return found;
        }

        private void ApplyOriginalSdkCompatibility(uint input, uint length, string? resource = null)
        {
            if (!ReferenceEquals(originalCompatibilityPackage, rootPackage))
            {
                originalCompatibilityPackage = rootPackage;
                sdkImages.Clear();
                sdkRuleMatches.Clear();
                sdkExpiryMatches.Clear();
            }
            var expiry = FindLoadedSdkExpiryGate(input, length, resource);
            foreach (SdkRuntimeRule rule in sdkRuntimeRules)
            {
                if (resource != null && resource != rule.Resource)
                {
                    continue;
                }
                Package originalPackage = rootPackage;
                if (!originalPackage.Contains(rule.Resource))
                {
                    if ((rule.Resource != "flaengine.ext" && rule.Resource != "skf_plugin.ext") ||
                        mythroad == null || length != rule.TemplateLength)
                        continue;
                    originalPackage = mythroad.FlashPlugin;
                }
                if (!sdkImages.TryGetValue(rule.Resource, out byte[]? original))
                {
                    original = originalPackage.Read(rule.Resource, diagnostic: Record);
                    sdkImages.Add(rule.Resource, original);
                }
                if (original.Length != length || length < 8)
                {
                    continue;
                }
                if (!sdkRuleMatches.TryGetValue(rule, out int delta))
                {
                    delta = int.MinValue;
                    foreach (var change in rule.Changes)
                    {
                        int match = FindSdkWindow(original, change.Window);
                        if (match < 8 || change.Before.Length != change.After.Length)
                        {
                            delta = int.MaxValue;
                            break;
                        }
                        int displacement = match + 32 - change.Offset;
                        if (delta != int.MinValue && delta != displacement)
                        {
                            delta = int.MaxValue;
                            break;
                        }
                        delta = displacement;
                    }
                    sdkRuleMatches.Add(rule, delta);
                }
                if (delta == int.MaxValue)
                {
                    continue;
                }
                if (!MatchesOriginalSdkImage(input, length, original))
                {
                    continue;
                }
                uint tailAddress = 0;
                uint oldTail = input + (uint)rule.TemplateLength;
                uint Map(uint target)
                {
                    if (target >= oldTail && (ulong)target < (ulong)oldTail + (uint)rule.Tail.Length)
                    {
                        return tailAddress + target - oldTail;
                    }
                    long translated = (long)target + delta;
                    if (translated < input || translated >= (long)input + length)
                    {
                        throw new RuntimeFault("SDK semantic branch target is outside the matched module");
                    }
                    return (uint)translated;
                }
                var key = (input, rule, delta);
                bool created = false;
                if (rule.Tail.Length > 0 && !sdkRuntimeTails.TryGetValue(key, out tailAddress))
                {
                    if (sdkRuntimeTails.Count >= 8 || rule.Tail.Length > 256)
                    {
                        throw new RuntimeFault("SDK compatibility trampoline limit");
                    }
                    tailAddress = Memory.NativeAllocate((uint)rule.Tail.Length);
                    if (tailAddress == 0)
                    {
                        throw new RuntimeFault("SDK compatibility trampoline allocation failed");
                    }
                    created = true;
                }
                try
                {
                    byte[] tail = (byte[])rule.Tail.Clone();
                    foreach (int offset in rule.TailBranches)
                    {
                        RelocateSdkBranch(tail, offset, oldTail + (uint)offset, tailAddress + (uint)offset, Map, rule.ThumbBranches);
                    }
                    var changes = new List<(uint Address, byte[] Bytes)>();
                    foreach (var change in rule.Changes)
                    {
                        uint address = input + (uint)(change.Offset + delta);
                        byte[] adapted = (byte[])change.After.Clone();
                        if (adapted.Length == 4 && IsSdkBranch(adapted, 0, rule.ThumbBranches))
                        {
                            RelocateSdkBranch(adapted, 0, input + (uint)change.Offset, address, Map, rule.ThumbBranches);
                        }
                        changes.Add((address, adapted));
                    }
                    if (tail.Length > 0)
                    {
                        Memory.Write(tailAddress, tail);
                    }
                    foreach (var change in changes)
                    {
                        Memory.Write(change.Address, change.Bytes);
                    }
                    if (created)
                    {
                        sdkRuntimeTails.Add(key, tailAddress);
                    }
                }
                catch
                {
                    if (created)
                    {
                        Memory.NativeFree(tailAddress, (uint)rule.Tail.Length);
                    }
                    throw;
                }
                Record($"compatibility semantic SDK: {rule.Resource} @{input:x8}; full instruction contexts, delta={delta}, trampoline={tailAddress:x8}; source bytes unchanged");
                break;
            }
            if (expiry.Address != 0 && Memory.U32(expiry.Address) == 0xb087b500)
            {
                Memory.Put32(expiry.Address, 0x47702001); // movs r0,#1; bx lr
                Record($"compatibility SDK expiry: {expiry.Resource} @{expiry.Address:x8}; predicate and helpers verified; real calendar and source bytes unchanged");
            }
        }

        private bool MatchesOriginalSdkImage(uint input, uint length, byte[] original)
        {
            Memory.Check(input, length);
            for (uint index = 8; index < length; index++)
            {
                bool capacityPatch = input == sdkCapacityCodeAddress && length == sdkCapacityCodeLength &&
                    (index == sdkCapacityPatchOffset || index == sdkCapacityPatchOffset + 1) &&
                    Memory.U16(input + sdkCapacityPatchOffset) == 0xe001;
                if (Memory.U8(input + index) != original[index] && !capacityPatch) return false;
            }
            return true;
        }

        private static bool IsSdkBranch(byte[] bytes, int offset, bool thumb)
        {
            uint word = SdkWord(bytes, offset);
            return thumb ? (word & 0xf800) == 0xf000 &&
                ((word >> 16 & 0xf800) == 0xf800 || (word >> 16 & 0xf800) == 0xe800) :
                (word & 0x0e000000) == 0x0a000000;
        }

        private static void RelocateSdkBranch(byte[] bytes, int offset, uint oldSource, uint newSource,
            Func<uint, uint> targetMap, bool thumb)
        {
            uint word = SdkWord(bytes, offset);
            uint result;
            if (!thumb && (word & 0x0e000000) == 0x0a000000)
            {
                int displacement = (int)(word << 8) >> 6;
                uint target = targetMap(unchecked((uint)(oldSource + 8L + displacement)));
                long delta = (long)target - newSource - 8;
                if ((delta & 3) != 0 || delta < -33554432 || delta > 33554428)
                {
                    throw new RuntimeFault("SDK ARM trampoline is out of branch range");
                }
                result = word & 0xff000000 | (uint)(delta >> 2) & 0x00ffffff;
            }
            else if (thumb && (word & 0xf800) == 0xf000 &&
                ((word >> 16 & 0xf800) == 0xf800 || (word >> 16 & 0xf800) == 0xe800))
            {
                bool exchange = (word >> 16 & 0xf800) == 0xe800;
                int displacement = (int)((word & 0x7ff) << 12 | (word >> 16 & 0x7ff) << 1);
                displacement = displacement << 9 >> 9;
                uint target = targetMap(unchecked((uint)((exchange ? oldSource & ~3u : oldSource) + 4L + displacement)));
                long delta = (long)target - (exchange ? newSource & ~3u : newSource) - 4;
                if ((delta & (exchange ? 3 : 1)) != 0 || delta < -4194304 || delta > 4194302)
                {
                    throw new RuntimeFault("SDK Thumb trampoline is out of branch range");
                }
                uint encoded = unchecked((uint)delta);
                result = 0xf000u | ((encoded >> 12) & 0x7ff) |
                    ((exchange ? 0xe800u : 0xf800u) | ((encoded >> 1) & 0x7ff)) << 16;
            }
            else
            {
                throw new RuntimeFault("SDK trampoline relocation is not a supported branch");
            }
            for (int index = 0; index < 4; index++)
            {
                bytes[offset + index] = (byte)(result >> (index * 8));
            }
        }

        private static uint SdkWord(byte[] bytes, int offset)
        {
            if (offset < 0 || offset > bytes.Length - 4)
            {
                throw new RuntimeFault("SDK trampoline instruction range");
            }
            return (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);
        }

        private static byte[] SdkHex(string value)
        {
            var bytes = new byte[value.Length / 2];
            for (int index = 0; index < bytes.Length; index++)
            {
                bytes[index] = byte.Parse(value.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            return bytes;
        }
    }
}

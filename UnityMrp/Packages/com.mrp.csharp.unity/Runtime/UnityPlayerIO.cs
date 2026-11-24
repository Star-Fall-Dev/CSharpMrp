#nullable enable
using System.IO;
using UnityEngine;

namespace CSharpMrp.Unity
{
    public static class UnityPlayerIO
    {
        public static IFileStorage Storage => SystemFileStorage.Instance;
        public static bool SupportsHostNetwork => Application.platform != RuntimePlatform.WebGLPlayer;
        public static string DefaultSaveRoot => Path.Combine(Application.persistentDataPath, "CSharpMrp");

        public static byte[] OpenPackage(string path)
        {
            using var stream = Storage.Open(path, FileMode.Open, FileAccess.Read);
            return PlayerFiles.ReadPackage(stream);
        }

        public static int GuestKey(KeyCode key)
        {
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
            {
                return key - KeyCode.Alpha0;
            }
            if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9)
            {
                return key - KeyCode.Keypad0;
            }
            return key switch
            {
                KeyCode.Minus or KeyCode.KeypadMultiply => 10,
                KeyCode.Equals or KeyCode.KeypadPlus => 11,
                KeyCode.UpArrow => 12,
                KeyCode.DownArrow => 13,
                KeyCode.LeftArrow => 14,
                KeyCode.RightArrow => 15,
                KeyCode.F1 => 17,
                KeyCode.F2 => 18,
                KeyCode.Return or KeyCode.KeypadEnter or KeyCode.Space => 20,
                KeyCode.Escape or KeyCode.Backspace => 23,
                _ => -1
            };
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CSharpMrp
{
    public interface IFramePresenter
    {
        // Little-endian RGB565, top row first. The caller reuses the buffer.
        bool Present(byte[] pixels, int width, int height);
    }

    public interface IPlayerInput
    {
        void Key(int type, int key);
        void Touch(int type, int x, int y);
        void EditText(uint handle, string text);
        void EditChoice(uint handle, int choice);
        Task<bool> SubmitEdit(uint handle, string text, int choice);
    }

    public sealed class PhoneKeys
    {
        public static IReadOnlyList<(string Label, int Key)> Layout { get; } = Array.AsReadOnly(new[]
        {
            ("L", 17), ("↑", 12), ("R", 18),
            ("←", 14), ("OK", 20), ("→", 15),
            ("Call", 19), ("↓", 13), ("Back", 23),
            ("1", 1), ("2", 2), ("3", 3),
            ("4", 4), ("5", 5), ("6", 6),
            ("7", 7), ("8", 8), ("9", 9),
            ("*", 10), ("0", 0), ("#", 11)
        });

        private readonly Dictionary<int, int> sources = new Dictionary<int, int>();
        private readonly int[] held = new int[27];
        private readonly Func<int, int, bool> sendKey;
        private uint pendingReleases;

        public PhoneKeys(IPlayerInput input) : this(input.Key) { }

        public PhoneKeys(Action<int, int> sendKey) : this((type, key) =>
        {
            sendKey(type, key);
            return true;
        }) { }

        public PhoneKeys(Func<int, int, bool> sendKey)
        {
            this.sendKey = sendKey;
        }

        public void Press(int source, int key)
        {
            if ((uint)key >= held.Length || sources.ContainsKey(source))
            {
                return;
            }
            if (held[key] == 0 && (!TryRelease(key) || !sendKey(0, key)))
            {
                return;
            }
            sources.Add(source, key);
            held[key]++;
        }

        public void Release(int source)
        {
            if (sources.TryGetValue(source, out int key))
            {
                sources.Remove(source);
                if (--held[key] == 0)
                {
                    pendingReleases |= 1u << key;
                    TryRelease(key);
                }
            }
        }

        public void ReleaseAll()
        {
            sources.Clear();
            for (int key = 0; key < held.Length; key++)
            {
                if (held[key] != 0)
                {
                    held[key] = 0;
                    pendingReleases |= 1u << key;
                }
            }
            RetryReleases();
        }

        public void RetryReleases()
        {
            for (int key = 0; pendingReleases != 0 && key < held.Length; key++)
            {
                if (!TryRelease(key)) break;
            }
        }

        private bool TryRelease(int key)
        {
            uint bit = 1u << key;
            if ((pendingReleases & bit) == 0) return true;
            if (!sendKey(1, key)) return false;
            pendingReleases &= ~bit;
            return true;
        }
    }
}

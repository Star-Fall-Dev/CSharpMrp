#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;

namespace CSharpMrp
{
    public sealed class SessionInput : IPlayerInput
    {
        private readonly PlayerSessionWorker session;

        public SessionInput(PlayerSessionWorker session)
        {
            this.session = session;
        }

        public void Key(int type, int key) => session.EnqueueKey(type, key, allowRetry: false);
        public bool TryKey(int type, int key) => session.EnqueueKey(type, key, allowRetry: true);
        public void Touch(int type, int x, int y) => session.EnqueueTouch(type, x, y);
        public void EditText(uint handle, string text) => session.Enqueue((host, now) => host.EditText(handle, text, now));
        public void EditChoice(uint handle, int choice) => session.Enqueue((host, now) => host.EditChoice(handle, choice, now));

        public bool TryTouchMapped(int type, int x, int y, int displayedWidth, int displayedHeight)
        {
            if (displayedWidth <= 0 || displayedHeight <= 0) return false;
            return session.TryEnqueueMappedTouch(type, x, y, displayedWidth, displayedHeight);
        }

        public Task<bool> SubmitEdit(uint handle, string text, int choice) =>
            session.InvokeAsync((host, now) => (choice != 0 || host.EditText(handle, text, now)) &&
                host.EditChoice(handle, choice, now));
    }

    public static class PlayerFiles
    {
        public static byte[] ReadPackage(Stream stream)
        {
            long estimate = 0;
            if (stream.CanSeek)
            {
                try { estimate = stream.Length - stream.Position; }
                catch (NotSupportedException) { } // Some stream wrappers cannot report a length.
            }
            byte[] bytes = estimate > 0 && estimate <= Package.MaxBytes ? new byte[(int)estimate] : Array.Empty<byte>();
            int used = 0;
            while (true)
            {
                if (used == bytes.Length)
                {
                    // Probe for EOF before growing. An exact size hint needs just the result array.
                    int next = stream.ReadByte();
                    if (next < 0) return bytes;
                    if (used == Package.MaxBytes) throw new IOException("The MRP exceeds the package size limit.");
                    int capacity = Math.Min(Package.MaxBytes, Math.Max(4096, used * 2));
                    Array.Resize(ref bytes, capacity);
                    bytes[used++] = (byte)next;
                }
                else
                {
                    int read = stream.Read(bytes, used, bytes.Length - used);
                    if (read == 0)
                    {
                        if (used == 0) return Array.Empty<byte>();
                        Array.Resize(ref bytes, used);
                        return bytes;
                    }
                    used += read;
                }
            }
        }

        public static PlayerGame DescribePackage(string filename)
        {
            string name = Path.GetFileName(filename);
            return new PlayerGame { id = name, title = name, filename = name, resource = "games/" + name };
        }

        public static MythroadFiles LoadMythroad(string directory, IFileStorage storage, string? fallbackDirectory = null,
            bool mountFiles = true)
        {
            byte[] Read(string relative)
            {
                using var stream = storage.Open(Path.Combine(directory, relative), FileMode.Open, FileAccess.Read);
                return ReadPackage(stream);
            }

            byte[]? Optional(string relative) => storage.FileExists(Path.Combine(directory, relative))
                ? Read(relative) : null;
            return new MythroadFiles(Read(MythroadFiles.Paths[0]), Read(MythroadFiles.Paths[1]),
                Read(MythroadFiles.Paths[2]), mountFiles ? Optional(MythroadFiles.NetpayPath) : null,
                mountFiles ? Optional(MythroadFiles.ReaderPath) : null,
                directory, storage, fallbackDirectory, mountFiles, takeOwnership: true);
        }
    }
}

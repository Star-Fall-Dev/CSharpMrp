using System;
using System.Collections.Generic;
using System.IO;

namespace CSharpMrp
{
    // Immutable built-in files, shared by sessions. Guest c:/ is the mythroad root.
    public sealed class MythroadFiles
    {
        public const string NetpayPath = "plugins/netpay.mrp";
        public const string ReaderPath = "ydqtwo.mrp";

        // Required resources used by existing catalogs; netpay remains optional.
        public static IReadOnlyList<string> Paths
        {
            get;
        } = Array.AsReadOnly(new[]
        {
            "system/gb12.uc2", "system/gb16.uc2", "plugins/flaengine.mrp"
        });
        private readonly byte[] font16;
        private readonly Dictionary<string, byte[]> builtinFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private readonly IFileStorage storage;
        internal Package FlashPlugin { get; }
        private readonly Dictionary<string, string> mountedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[]?> directories = new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase);

        public MythroadFiles(byte[] gb12, byte[] gb16, byte[] flash, byte[]? netpay = null,
            byte[]? reader = null, string? packageRoot = null, IFileStorage? storage = null, string? fallbackRoot = null,
            bool mountFiles = true)
            : this(gb12, gb16, flash, netpay, reader, packageRoot, storage, fallbackRoot, mountFiles, takeOwnership: false) { }

        internal MythroadFiles(byte[] gb12, byte[] gb16, byte[] flash, byte[]? netpay, byte[]? reader,
            string? packageRoot, IFileStorage? storage, string? fallbackRoot, bool mountFiles, bool takeOwnership)
        {
            byte[] Store(byte[] data) => takeOwnership ? data : (byte[])data.Clone();
            this.storage = storage ?? SystemFileStorage.Instance;
            if (gb12.Length != 65535 * 18 && gb12.Length != 65536 * 18)
            {
                throw new RuntimeFault("invalid gb12.uc2 font length");
            }
            if (gb16.Length != 65536 * 32)
            {
                throw new RuntimeFault("invalid gb16.uc2 font length");
            }
            byte[] flashImage = Store(flash);
            FlashPlugin = Package.FromOwnedBytes(flashImage);
            if (!FlashPlugin.Contains("skf_plugin.ext") || !FlashPlugin.Contains("flaengine.ext"))
            {
                throw new RuntimeFault("invalid flaengine plugin package");
            }
            font16 = Store(gb16);
            if (!mountFiles)
            {
                // The guest already sees the files through its writable directory.
                // Keep the font and Flash data available for runtime compatibility.
                return;
            }
            builtinFiles.Add(Paths[0], Store(gb12));
            builtinFiles.Add(Paths[1], font16);
            builtinFiles.Add(Paths[2], flashImage);
            if (netpay != null)
            {
                byte[] paymentImage = Store(netpay);
                var payment = Package.FromOwnedBytes(paymentImage);
                if (!payment.Contains("netpay.ext"))
                {
                    throw new RuntimeFault("invalid netpay plugin package");
                }
                builtinFiles.Add(NetpayPath, paymentImage);
            }
            if (reader != null)
            {
                byte[] readerImage = Store(reader);
                var reading = Package.FromOwnedBytes(readerImage);
                if (!reading.Contains("start.mr"))
                {
                    throw new RuntimeFault("invalid ydqtwo reader package");
                }
                builtinFiles.Add(ReaderPath, readerImage);
            }
            var directoryEntries = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            AddDirectory(directoryEntries, "plugins");
            AddDirectory(directoryEntries, "system");
            foreach (string path in builtinFiles.Keys) AddDirectoryEntry(directoryEntries, path);
            MountResources(packageRoot, directoryEntries);
            MountResources(fallbackRoot, directoryEntries);
            foreach (var directory in directoryEntries)
            {
                HashSet<string> names = directory.Value;
                if (names.Count > 4096)
                {
                    // Keep the directory visible and report the limit if the guest lists it.
                    directories.Add(directory.Key, null);
                    continue;
                }
                names.Add(".");
                names.Add("..");
                var entries = new string[names.Count];
                names.CopyTo(entries);
                Array.Sort(entries, StringComparer.Ordinal);
                directories.Add(directory.Key, entries);
            }
        }

        public BitmapFont CreateFont()
        {
            return BitmapFont.FromImmutableUc2(font16);
        }

        private static void AddDirectory(Dictionary<string, HashSet<string>> directoryEntries, string path)
        {
            if (directoryEntries.ContainsKey(path)) return;
            directoryEntries.Add(path, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (path.Length != 0) AddDirectoryEntry(directoryEntries, path);
        }

        private static void AddDirectoryEntry(Dictionary<string, HashSet<string>> directoryEntries, string path)
        {
            int slash = path.LastIndexOf('/');
            string directory = slash < 0 ? "" : path.Substring(0, slash);
            AddDirectory(directoryEntries, directory);
            directoryEntries[directory].Add(path.Substring(slash + 1));
        }

        private void CheckMountedLinks(string path)
        {
            for (string? entry = path; entry != null; entry = Path.GetDirectoryName(entry))
            {
                if (storage.IsLink(entry)) throw new IOException("mounted package path contains a link");
            }
        }

        private void MountResources(string? root, Dictionary<string, HashSet<string>> directoryEntries)
        {
            if (string.IsNullOrEmpty(root)) return;
            string directory = Path.GetFullPath(root);
            if (!storage.DirectoryExists(directory)) return;
            CheckMountedLinks(directory);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<(string Host, string Guest)>();
            pending.Push((directory, ""));
            while (pending.Count != 0)
            {
                var folder = pending.Pop();
                foreach (string entry in storage.EnumerateEntries(folder.Host))
                {
                    string name = Path.GetFileName(entry);
                    string host = Path.Combine(folder.Host, name);
                    if (storage.IsLink(host)) continue;
                    string path = folder.Guest + name;
                    if (storage.DirectoryExists(host))
                    {
                        pending.Push((host, path + "/"));
                        continue;
                    }
                    bool package = name.EndsWith(".mrp", StringComparison.OrdinalIgnoreCase);
                    bool systemFont = folder.Guest.Equals("system/", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".uc2", StringComparison.OrdinalIgnoreCase);
                    bool nesImage = folder.Guest.StartsWith("nes/", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".nes", StringComparison.OrdinalIgnoreCase);
                    if (!package && !systemFont && !nesImage) continue;
                    // Imported root packages have a legacy 32-byte filename alias.
                    bool imported = package && folder.Guest.Length == 0 && name.Length > 22 &&
                        name.StartsWith("user_", StringComparison.Ordinal) && name[21] == '_';
                    for (int i = 5; imported && i < 21; i++) imported = Uri.IsHexDigit(name[i]);
                    AddResource(path, host, list: !imported);
                    if (imported) AddResource(name.Substring(0, 21) + ".mrp", host);
                }
            }

            void AddResource(string path, string host, bool list = true)
            {
                if (!names.Add(path)) throw new RuntimeFault("mounted package filename collision: " + path);
                if (mountedFiles.TryAdd(path, host) && list) AddDirectoryEntry(directoryEntries, path);
            }
        }

        private string? MountedResource(string path)
        {
            if (!mountedFiles.TryGetValue(path, out string? candidate) || !storage.FileExists(candidate)) return null;
            CheckMountedLinks(candidate);
            return candidate;
        }

        internal bool Protected(string path)
        {
            return directories.ContainsKey(path) || builtinFiles.ContainsKey(path) || mountedFiles.ContainsKey(path);
        }

        internal uint Info(string path)
        {
            return builtinFiles.ContainsKey(path) || MountedResource(path) != null ? 1u : directories.ContainsKey(path) ? 2u : 8u;
        }

        internal uint Length(string path)
        {
            if (builtinFiles.TryGetValue(path, out var bytes)) return (uint)bytes.Length;
            string? mounted = MountedResource(path);
            return mounted == null ? uint.MaxValue : checked((uint)storage.GetLength(mounted));
        }

        internal Stream? Open(string path)
        {
            if (builtinFiles.TryGetValue(path, out var bytes)) return new MemoryStream(bytes, false);
            string? mounted = MountedResource(path);
            return mounted == null ? null : storage.Open(mounted, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        internal ReadOnlyMemory<string> DirectoryEntries(string path)
        {
            if (!directories.TryGetValue(path, out var names)) return default;
            return names ?? throw new RuntimeFault("mounted package directory entry limit");
        }
    }
}

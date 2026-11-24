#nullable enable
using System.Collections.Generic;
using System.IO;

namespace CSharpMrp
{
    public interface IFileStorage
    {
        bool FileExists(string path);
        bool DirectoryExists(string path);
        bool IsLink(string path);
        long GetLength(string path);
        Stream Open(string path, FileMode mode, FileAccess access, FileShare share = FileShare.Read);
        IEnumerable<string> EnumerateEntries(string path);
        IEnumerable<string> EnumerateFiles(string path, string pattern);
        void CreateDirectory(string path);
        void DeleteFile(string path);
        void DeleteDirectory(string path);
        void MoveFile(string source, string destination);
        void MoveDirectory(string source, string destination);
    }

    public class SystemFileStorage : IFileStorage
    {
        public static SystemFileStorage Instance { get; } = new SystemFileStorage();

        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        public long GetLength(string path) => new FileInfo(path).Length;
        public Stream Open(string path, FileMode mode, FileAccess access, FileShare share = FileShare.Read) =>
            new FileStream(path, mode, access, share);
        public IEnumerable<string> EnumerateEntries(string path) => Directory.EnumerateFileSystemEntries(path);
        public IEnumerable<string> EnumerateFiles(string path, string pattern) => Directory.EnumerateFiles(path, pattern);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public void DeleteFile(string path) => File.Delete(path);
        public void DeleteDirectory(string path) => Directory.Delete(path, false);
        public void MoveFile(string source, string destination) => File.Move(source, destination);
        public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);
    }
}

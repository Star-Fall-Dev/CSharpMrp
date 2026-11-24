using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Callbacks;
using UnityEngine;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace CSharpMrp.Unity.Editor
{
    /// <summary>Retain IL2CPP assembly resources omitted by Tuanjie's WebGL data packager.</summary>
    public static class CSharpMrpWebEncoding
    {
        [PostProcessBuild(1100)]
        public static void OnBuild(BuildTarget target, string output)
        {
            if (target == BuildTarget.WebGL)
            {
                Repair(output);
            }
        }

        public static void Repair(string output)
        {
            string resources = "Library/Bee/artifacts/WebGL/il2cppOutput/data/Resources";
            string[] files = Directory.GetFiles(resources, "*-resources.dat");
            if (!files.Any(file => Path.GetFileName(file) == "I18N.CJK.dll-resources.dat"))
            {
                throw new BuildFailedException("IL2CPP did not generate the required CJK encoding tables.");
            }
            string container = Directory.GetFiles(Path.Combine(output, "Build"), "*.data*").Single();
            var compression = PlayerSettings.WebGL.compressionFormat;
            byte[] data;
            using (var input = File.OpenRead(container))
            using (var decoded = Decode(input, compression))
            using (var buffer = new MemoryStream())
            {
                decoded.CopyTo(buffer);
                data = buffer.ToArray();
            }
            using var source = new MemoryStream(data);
            using var reader = new BinaryReader(source);
            var magic = new List<byte>();
            byte value;
            while ((value = reader.ReadByte()) != 0 && magic.Count < 32)
            {
                magic.Add(value);
            }
            string signature = Encoding.ASCII.GetString(magic.ToArray());
            if (value != 0 || (signature != "TuanjieWebData1.0" && signature != "UnityWebData1.0"))
            {
                throw new BuildFailedException("Unknown WebGL data archive signature: " + signature);
            }
            magic.Add(0);
            int header = reader.ReadInt32();
            if (header < source.Position || header > data.Length)
            {
                throw new BuildFailedException("Invalid WebGL data archive header.");
            }
            var entries = new List<(int Offset, int Length, byte[] Name)>();
            while (source.Position < header)
            {
                int offset = reader.ReadInt32();
                int length = reader.ReadInt32();
                int nameLength = reader.ReadInt32();
                if (nameLength < 1 || nameLength > header - source.Position || offset < header
                    || length < 0 || (long)offset + length > data.Length)
                {
                    throw new BuildFailedException("Invalid WebGL data archive entry.");
                }
                entries.Add((offset, length, reader.ReadBytes(nameLength)));
            }
            var names = new HashSet<string>(entries.Select(entry => Encoding.UTF8.GetString(entry.Name)));
            var additions = files.OrderBy(file => file, StringComparer.Ordinal)
                .Select(file => (Name: Encoding.UTF8.GetBytes("Il2CppData/Resources/" + Path.GetFileName(file)),
                    Data: File.ReadAllBytes(file)))
                .Where(file => !names.Contains(Encoding.UTF8.GetString(file.Name))).ToArray();
            if (additions.Length == 0)
            {
                return;
            }
            int delta = additions.Sum(file => 12 + file.Name.Length);
            using var repaired = new MemoryStream();
            using (var writer = new BinaryWriter(repaired, Encoding.UTF8, true))
            {
                writer.Write(magic.ToArray());
                writer.Write(checked(header + delta));
                foreach (var entry in entries)
                {
                    writer.Write(checked(entry.Offset + delta));
                    writer.Write(entry.Length);
                    writer.Write(entry.Name.Length);
                    writer.Write(entry.Name);
                }
                int offset = checked(data.Length + delta);
                foreach (var file in additions)
                {
                    writer.Write(offset);
                    writer.Write(file.Data.Length);
                    writer.Write(file.Name.Length);
                    writer.Write(file.Name);
                    offset = checked(offset + file.Data.Length);
                }
                writer.Write(data, header, data.Length - header);
                foreach (var file in additions)
                {
                    writer.Write(file.Data);
                }
            }
            repaired.Position = 0;
            using (var destination = File.Create(container))
            using (var encoded = Encode(destination, compression))
            {
                repaired.CopyTo(encoded);
            }
            Debug.Log("[CSharpMrpWebEncoding] Added " + additions.Length + " assembly resource files to " + container);
        }

        private static Stream Decode(Stream stream, WebGLCompressionFormat format)
        {
            if (format == WebGLCompressionFormat.Gzip)
            {
                return new GZipStream(stream, CompressionMode.Decompress, true);
            }
            if (format == WebGLCompressionFormat.Brotli)
            {
                return new BrotliStream(stream, CompressionMode.Decompress, true);
            }
            return stream;
        }

        private static Stream Encode(Stream stream, WebGLCompressionFormat format)
        {
            if (format == WebGLCompressionFormat.Gzip)
            {
                return new GZipStream(stream, CompressionLevel.Optimal, true);
            }
            if (format == WebGLCompressionFormat.Brotli)
            {
                return new BrotliStream(stream, CompressionLevel.Optimal, true);
            }
            return stream;
        }
    }
}

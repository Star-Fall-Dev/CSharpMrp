using System.IO;
using System.IO.Compression;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

internal sealed class CSharpMrpWebBuild : BuildPlayerProcessor
{
    public override int callbackOrder => 0;

    public override void PrepareForBuild(BuildPlayerContext context)
    {
        if (context.BuildPlayerOptions.target != BuildTarget.WebGL) return;

        string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "mythroad"));
        foreach (string required in CSharpMrp.MythroadFiles.Paths)
        {
            if (!File.Exists(Path.Combine(root, required)))
                throw new BuildFailedException("Missing Mythroad resource: " + required);
        }

        string archive = Path.GetFullPath(Path.Combine("Library", CSharpMrpWebFiles.ResourceArchive));
        using (var stream = File.Create(archive))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (string name in new[] { "dsm_gm.mrp", CSharpMrp.MythroadFiles.ReaderPath })
            {
                if (File.Exists(Path.Combine(root, name))) Add(Path.Combine(root, name));
            }
            foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "system")))
            {
                string extension = Path.GetExtension(path);
                if (extension == ".uc2" || extension == ".adl") Add(path);
            }
            foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "plugins"), "*.mrp",
                         SearchOption.AllDirectories))
                Add(path);

            void Add(string path)
            {
                string name = path.Substring(root.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                var entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
                using var source = File.OpenRead(path);
                using var target = entry.Open();
                source.CopyTo(target);
            }
        }
        context.AddAdditionalPathToStreamingAssets(archive, CSharpMrpWebFiles.ResourceArchive);
    }
}

#nullable enable
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CSharpMrp;
using CSharpMrp.Unity;
using UnityEngine.Networking;
using UnityEngine.Scripting;
#endif

public sealed class CSharpMrpWebFiles : MonoBehaviour
{
    public const string ResourceArchive = "csharpmrp-mythroad.zip";

#if UNITY_WEBGL && !UNITY_EDITOR
    private TaskCompletionSource<PickedFile?>? picker;
    private string? requestId;
    private TaskCompletionSource<bool>? sync;
    private MythroadFiles? resources;

    [DllImport("__Internal")]
    private static extern void MRPWebChooseFile(string receiver, string request, string cache,
        int maxBytes, string choose, string cancel);

    [DllImport("__Internal")]
    private static extern void MRPWebCancelFilePicker();

    [DllImport("__Internal")]
    private static extern void MRPWebFlushFileSystem(string receiver);

    internal async Task<MythroadFiles> PrepareMythroadAsync(string root)
    {
        if (resources != null) return resources;
        string url = Application.streamingAssetsPath.TrimEnd('/') + "/" + ResourceArchive;
        using var request = UnityWebRequest.Get(url);
        var ready = new TaskCompletionSource<bool>();
        request.SendWebRequest().completed += _ => ready.TrySetResult(true);
        await ready.Task;
        if (request.result != UnityWebRequest.Result.Success)
            throw new IOException("Mythroad resources could not be loaded: " + request.error);

        string destination = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        using var buffer = new MemoryStream(request.downloadHandler.data, writable: false);
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            string path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!path.StartsWith(destination, StringComparison.Ordinal) || entry.Name.Length == 0)
                throw new InvalidDataException("Invalid path in the Mythroad resource archive.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var source = entry.Open();
            using var target = File.Create(path);
            source.CopyTo(target);
        }
        resources = PlayerFiles.LoadMythroad(root, UnityPlayerIO.Storage);
        return resources;
    }

    internal async Task<PickedFile?> PickAsync()
    {
        if (picker != null) throw new InvalidOperationException("A file picker is already open.");
        var completion = new TaskCompletionSource<PickedFile?>();
        picker = completion;
        requestId = Guid.NewGuid().ToString("N");
        try
        {
            MRPWebChooseFile(gameObject.name, requestId, Application.temporaryCachePath,
                CSharpMrp.Package.MaxBytes, "Choose MRP", "Cancel");
            return await completion.Task;
        }
        finally
        {
            picker = null;
            requestId = null;
        }
    }

    [Preserve]
    public void OnFilePicked(string json)
    {
        var completion = picker;
        if (completion == null) return;
        try
        {
            var reply = JsonUtility.FromJson<PickerReply>(json);
            if (reply.request != requestId) return;
            if (reply.status == "cancel")
            {
                completion.TrySetResult(null);
                return;
            }
            if (reply.status != "ok") throw new IOException(reply.message);
            string expected = Path.GetFullPath(Path.Combine(Application.temporaryCachePath, $"web-picker-{requestId}.mrp"));
            if (Path.GetFullPath(reply.path) != expected)
                throw new IOException("The browser returned an unexpected import path.");
            completion.TrySetResult(new PickedFile(expected, Path.GetFileName(reply.name)));
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
    }

    internal async Task FlushAsync()
    {
        if (sync is { } pending)
        {
            await pending.Task;
            return;
        }
        var completion = new TaskCompletionSource<bool>();
        sync = completion;
        try
        {
            MRPWebFlushFileSystem(gameObject.name);
            await completion.Task;
        }
        finally
        {
            sync = null;
        }
    }

    [Preserve]
    public void OnWebFileSystemSynced(string error)
    {
        if (string.IsNullOrEmpty(error)) sync?.TrySetResult(true);
        else sync?.TrySetException(new IOException("Browser storage sync failed: " + error));
    }

    private void OnDestroy()
    {
        if (picker != null) MRPWebCancelFilePicker();
        picker?.TrySetCanceled();
        sync?.TrySetCanceled();
    }

    internal sealed class PickedFile : IDisposable
    {
        public readonly string Path;
        public readonly string Name;

        public PickedFile(string path, string name)
        {
            Path = path;
            Name = name;
        }

        public void Dispose() => File.Delete(Path);
    }

    [Serializable]
    private sealed class PickerReply
    {
        public string request = "";
        public string status = "";
        public string path = "";
        public string name = "";
        public string message = "";
    }
#endif
}

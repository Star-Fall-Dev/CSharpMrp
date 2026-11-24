#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;

namespace CSharpMrp.Unity
{
    public static class UnitySoundBank
    {
        private static readonly SemaphoreSlim loading = new SemaphoreSlim(1, 1);

        /// <summary>Read the Core package's bank on a worker when the player supports threads.</summary>
        public static async Task<MrpSoundDecoder> PrepareAsync(CancellationToken cancellation = default)
        {
            await loading.WaitAsync(cancellation);
            try
            {
                var asset = Resources.Load<TextAsset>("CSharpMrpGMBank");
                if (asset == null) throw new InvalidDataException("The CSharpMrp Core GM bank is missing.");
                try
                {
                    var bytes = asset.GetData<byte>();
                    var decoder = new MrpSoundDecoder(() => new BankStream(bytes));
#if UNITY_WEBGL && !UNITY_EDITOR
                    decoder.PrepareBank();
#else
                    // Keep the TextAsset loaded until the worker has stopped reading its native storage.
                    await Task.Run(decoder.PrepareBank, cancellation);
#endif
                    cancellation.ThrowIfCancellationRequested();
                    return decoder;
                }
                finally
                {
                    Resources.UnloadAsset(asset);
                }
            }
            finally
            {
                loading.Release();
            }
        }

        private sealed class BankStream : Stream
        {
            private readonly NativeArray<byte> bytes;
            private int position;

            public BankStream(NativeArray<byte> bytes) => this.bytes = bytes;
            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => bytes.Length;

            public override long Position
            {
                get => position;
                set
                {
                    if (value < 0 || value > int.MaxValue) throw new IOException("Invalid GM bank stream position.");
                    position = (int)value;
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int length = Math.Min(count, bytes.Length - position);
                if (length <= 0) return 0;
                NativeArray<byte>.Copy(bytes, position, buffer, offset, length);
                position += length;
                return length;
            }

            public override int ReadByte() => position < bytes.Length ? bytes[position++] : -1;

            public override long Seek(long offset, SeekOrigin origin)
            {
                Position = checked(offset + (origin switch
                {
                    SeekOrigin.Begin => 0,
                    SeekOrigin.Current => Position,
                    SeekOrigin.End => Length,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin)),
                }));
                return Position;
            }

            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}

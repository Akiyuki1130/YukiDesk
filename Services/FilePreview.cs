using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopBar.Interop;
using DesktopBar.UI;

namespace DesktopBar.Services
{
    internal static class FilePreview
    {
        internal const string WorkerArgument = "--file-preview";
        private static readonly SemaphoreSlim Slots = new(2);
        private const int PreviewSide = 512;
        private const int PixelHeaderSize = 12;
        private const uint PixelSignature = 0x31504459; // YDP1

        internal static async Task<BitmapSource> ReadAsync(string path, CancellationToken cancellation)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            budget.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await Slots.WaitAsync(budget.Token).ConfigureAwait(false);
                try { return await Task.Run(() => ReadInWorkerAsync(path, budget.Token)).ConfigureAwait(false); }
                finally { Slots.Release(); }
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                App.Log("读取文件预览失败: " + ex.Message);
                return null;
            }
        }

        private static async Task<BitmapSource> ReadInWorkerAsync(string path, CancellationToken cancellation)
        {
            var startInfo = new ProcessStartInfo(Path.ChangeExtension(typeof(App).Assembly.Location, ".exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            startInfo.ArgumentList.Add(WorkerArgument);
            startInfo.ArgumentList.Add(path);
            return await ReadWorkerOutputAsync(startInfo, cancellation).ConfigureAwait(false);
        }

        private static async Task<BitmapSource> ReadWorkerOutputAsync(ProcessStartInfo startInfo, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            using var worker = Process.Start(startInfo);
            if (worker == null) return null;
            using var stop = cancellation.UnsafeRegister(static state =>
                ThreadPool.UnsafeQueueUserWorkItem(StopWorker, (Process)state, preferLocal: false), worker);
            try
            {
                var pipe = worker.StandardOutput.BaseStream;
                var header = new byte[PixelHeaderSize];
                if (await pipe.ReadAtLeastAsync(header, header.Length, false, cancellation).ConfigureAwait(false) != header.Length
                    || BinaryPrimitives.ReadUInt32LittleEndian(header) != PixelSignature) return null;
                int width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                int height = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
                if (width is < 1 or > PreviewSide || height is < 1 or > PreviewSide) return null;
                int stride = width * 4;
                var pixels = new byte[stride * height];
                if (await pipe.ReadAtLeastAsync(pixels, pixels.Length, false, cancellation).ConfigureAwait(false) != pixels.Length
                    || await pipe.ReadAsync(header.AsMemory(0, 1), cancellation).ConfigureAwait(false) != 0) return null;
                await worker.WaitForExitAsync(cancellation).ConfigureAwait(false);
                if (worker.ExitCode != 0) return null;
                var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
                bitmap.Freeze();
                return bitmap;
            }
            finally
            {
                // The process owns the decoder and shell extension. Ending it frees
                // the slot even when the provider cannot return from its native call.
                StopWorker(worker);
            }
        }

        private static void StopWorker(Process worker)
        {
            try { if (!worker.HasExited) worker.Kill(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        internal static int RunWorker(string path)
        {
            using var watchdog = new Timer(_ => Environment.Exit(1), null, 4500, Timeout.Infinite);
            try
            {
                var image = FileImage.ReadThumbnail(path, PreviewSide) ?? NativeMethods.GetShellThumbnail(path, PreviewSide);
                if (image == null) return 1;
                int side = Math.Max(image.PixelWidth, image.PixelHeight);
                if (side > PreviewSide)
                {
                    double ratio = (double)PreviewSide / side;
                    image = new TransformedBitmap(image, new ScaleTransform(ratio, ratio));
                }
                if (image.Format != PixelFormats.Pbgra32)
                    image = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
                int stride = image.PixelWidth * 4;
                var pixels = new byte[stride * image.PixelHeight];
                image.CopyPixels(pixels, stride, 0);
                Span<byte> header = stackalloc byte[PixelHeaderSize];
                BinaryPrimitives.WriteUInt32LittleEndian(header, PixelSignature);
                BinaryPrimitives.WriteInt32LittleEndian(header[4..], image.PixelWidth);
                BinaryPrimitives.WriteInt32LittleEndian(header[8..], image.PixelHeight);
                using var output = Console.OpenStandardOutput();
                // 私有管道直传受限尺寸的像素，避免在两个进程间重复编解码 PNG。
                output.Write(header);
                output.Write(pixels);
                return 0;
            }
            catch { return 1; }
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using DesktopBar.Interop;
using DesktopBar.UI;

namespace DesktopBar.Services
{
    internal static class FilePreview
    {
        internal const string WorkerArgument = "--file-preview";
        private static readonly SemaphoreSlim Slots = new(2);
        private const int MaxPngBytes = 8 * 1024 * 1024;

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
            using var stop = cancellation.Register(() => StopWorker(worker));
            try
            {
                using var png = new MemoryStream();
                var pipe = worker.StandardOutput.BaseStream;
                var buffer = new byte[16384];
                int count;
                while ((count = await pipe.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) > 0)
                {
                    if (png.Length + count > MaxPngBytes) return null;
                    png.Write(buffer, 0, count);
                }
                await worker.WaitForExitAsync(cancellation).ConfigureAwait(false);
                if (worker.ExitCode != 0 || png.Length == 0) return null;
                png.Position = 0;
                var bitmap = BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
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
                var image = FileImage.ReadThumbnail(path, 512) ?? NativeMethods.GetShellThumbnail(path, 512);
                if (image == null) return 1;
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var png = new MemoryStream();
                encoder.Save(png);
                using var output = Console.OpenStandardOutput();
                png.WriteTo(output);
                return 0;
            }
            catch { return 1; }
        }
    }
}

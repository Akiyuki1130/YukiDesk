using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopBar.Interop
{
    internal static class ShellFiles
    {
        internal const string RecycleBin = "shell:::{645FF040-5081-101B-9F08-00AA002F954E}";
        internal sealed record RecycledItem(string Name, byte[] Id, bool IsFolder, string OriginalFolder, DateTime? DeletedAtUtc);
        internal readonly record struct RecycleTotals(long Count, long Bytes);
        private static readonly PropertyKey OriginalFolderKey = FindProperty("System.Recycle.DeletedFrom");
        private static readonly PropertyKey DeletedAtKey = FindProperty("System.Recycle.DateDeleted");

        internal static void Invoke(IntPtr owner, string path, string verb)
            => WithPath(path, id => InvokeItem(owner, id, verb));

        internal static void Invoke(IntPtr owner, RecycledItem item, string verb)
            => WithId(item.Id, id => InvokeItem(owner, id, verb));

        internal static void Invoke(IntPtr owner, IReadOnlyList<string> paths, string verb)
            => WithPathSelection(paths, (folder, children) => InvokeItems(owner, folder, children, verb));

        internal static void Invoke(IntPtr owner, IReadOnlyList<RecycledItem> items, string verb)
            => WithSelection(items, item =>
            {
                var pointer = Marshal.AllocCoTaskMem(item.Id.Length);
                Marshal.Copy(item.Id, 0, pointer, item.Id.Length);
                return pointer;
            }, (folder, children) => InvokeItems(owner, folder, children, verb));

        private static void InvokeItem(IntPtr owner, IntPtr id, string verb)
            => WithParent(id, (folder, child) => InvokeItems(owner, folder, new[] { child }, verb));

        private static void InvokeItems(IntPtr owner, IShellFolder folder, IntPtr[] children, string verb)
        {
            var iid = typeof(IContextMenu).GUID;
            Check(folder.GetUIObjectOf(owner, (uint)children.Length, children, ref iid, IntPtr.Zero, out var pointer));
            WithMenu(pointer, owner, verb);
        }

        internal static void Paste(IntPtr owner, string directory)
            => WithPath(directory, id =>
            {
                var folder = BindFolder(id);
                try
                {
                    var iid = typeof(IContextMenu).GUID;
                    Check(folder.CreateViewObject(owner, ref iid, out var pointer));
                    WithMenu(pointer, owner, "paste");
                }
                finally { Marshal.ReleaseComObject(folder); }
            });

        private static void WithMenu(IntPtr pointer, IntPtr owner, string verb)
        {
            var context = TakeInterface<IContextMenu>(pointer);
            var menu = CreatePopupMenu();
            var command = Marshal.StringToHGlobalAnsi(verb);
            try
            {
                const uint CanRename = 0x10;
                Check(context.QueryContextMenu(menu, 0, 1, 0x7fff, CanRename));
                var invocation = new Command
                {
                    Size = Marshal.SizeOf<Command>(), Owner = owner, Verb = command, Show = 1
                };
                Check(context.InvokeCommand(ref invocation));
            }
            finally
            {
                Marshal.FreeHGlobal(command);
                DestroyMenu(menu);
                Marshal.ReleaseComObject(context);
            }
        }

        internal static void Rename(IntPtr owner, string path, string name)
            => WithPath(path, id => WithParent(id, (folder, child) =>
            {
                const uint InFolder = 1, ForParsing = 0x8000;
                Check(folder.SetNameOf(owner, child, name, InFolder | ForParsing, out var renamed));
                Marshal.FreeCoTaskMem(renamed);
            }));

        internal static void EmptyRecycleBin(IntPtr owner)
        {
            int result = SHEmptyRecycleBin(owner, null, 0);
            const int Empty = unchecked((int)0x8000ffff);
            if (result != Empty) Check(result);
        }

        internal static void Drag(IntPtr owner, string path)
            => WithPath(path, id => Drag(owner, id, 7));

        internal static void Drag(IntPtr owner, RecycledItem item)
            => WithId(item.Id, id => Drag(owner, id, 2));

        internal static void Drag(IntPtr owner, IReadOnlyList<string> paths)
            => WithPathSelection(paths, (folder, children) => Drag(owner, folder, children, 7));

        private static void Drag(IntPtr owner, IntPtr id, uint effects)
            => WithParent(id, (folder, child) => Drag(owner, folder, new[] { child }, effects));

        private static void Drag(IntPtr owner, IShellFolder folder, IntPtr[] children, uint effects)
        {
            var iid = typeof(IDataObject).GUID;
            Check(folder.GetUIObjectOf(owner, (uint)children.Length, children, ref iid, IntPtr.Zero, out var data));
            try
            {
                EnableAsyncTransfer(data);
                Check(SHDoDragDrop(owner, data, IntPtr.Zero, effects, out _));
            }
            finally { Marshal.Release(data); }
        }

        private static void WithPathSelection(IReadOnlyList<string> paths, Action<IShellFolder, IntPtr[]> action)
        {
            if (paths.Count == 0) return;
            string parent = Path.GetDirectoryName(paths[0]);
            foreach (string path in paths)
                if (!string.Equals(parent, Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("请在同一目录中选择项目。");
            WithSelection(paths, path =>
            {
                Check(NativeMethods.SHParseDisplayName(path, IntPtr.Zero, out var id, 0, out _));
                return id;
            }, action);
        }

        private static void WithSelection<T>(IReadOnlyList<T> items, Func<T, IntPtr> getId, Action<IShellFolder, IntPtr[]> action)
        {
            if (items.Count == 0) return;
            var ids = new IntPtr[items.Count];
            try
            {
                for (int i = 0; i < items.Count; i++) ids[i] = getId(items[i]);
                WithParent(ids[0], (folder, first) =>
                {
                    var children = new IntPtr[ids.Length];
                    children[0] = first;
                    for (int i = 1; i < ids.Length; i++) children[i] = ILFindLastID(ids[i]);
                    action(folder, children);
                });
            }
            finally
            {
                foreach (var id in ids) Marshal.FreeCoTaskMem(id);
            }
        }

        [DllImport("shell32.dll")]
        private static extern IntPtr ILFindLastID(IntPtr id);

        private static void EnableAsyncTransfer(IntPtr data)
        {
            var iid = typeof(IDataObjectAsyncCapability).GUID;
            if (Marshal.QueryInterface(data, ref iid, out var pointer) < 0) return;
            var capability = TakeInterface<IDataObjectAsyncCapability>(pointer);
            try { capability.SetAsyncMode(true); }
            finally { Marshal.ReleaseComObject(capability); }
        }

        internal static void RegisterDropTarget(IntPtr window, IDropTarget target)
        {
            Check(OleInitialize(IntPtr.Zero));
            try
            {
                // HwndSource registers its WPF adapter on creation. Replace it only on our file-drop windows.
                const int AlreadyRegistered = unchecked((int)0x80040101);
                int result = RegisterDragDrop(window, target);
                if (result == AlreadyRegistered)
                {
                    Check(RevokeDragDrop(window));
                    result = RegisterDragDrop(window, target);
                }
                Check(result);
            }
            catch
            {
                OleUninitialize();
                throw;
            }
        }

        internal static void UnregisterDropTarget(IntPtr window)
        {
            RevokeDragDrop(window);
            OleUninitialize();
        }

        internal sealed class DropSession : IDisposable
        {
            private readonly IDropTarget _target;
            private readonly IDataObject _data;
            private bool _entered;
            internal uint EnterKeys { get; private set; }

            private DropSession(IDropTarget target, IDataObject data)
            {
                _target = target;
                _data = data;
            }

            internal static DropSession Create(IntPtr owner, string path, IDataObject data)
            {
                IDropTarget target = null;
                WithPath(path, id => WithParent(id, (folder, child) =>
                {
                    var iid = typeof(IDropTarget).GUID;
                    Check(folder.GetUIObjectOf(owner, 1, new[] { child }, ref iid, IntPtr.Zero, out var pointer));
                    target = TakeInterface<IDropTarget>(pointer);
                }));
                return new DropSession(target, data);
            }

            internal uint Enter(uint keys, ScreenPoint point, uint effects)
            {
                Check(_target.DragEnter(_data, keys, point, ref effects));
                _entered = true;
                EnterKeys = keys;
                return effects;
            }

            internal uint Over(uint keys, ScreenPoint point, uint effects)
            {
                Check(_target.DragOver(keys, point, ref effects));
                return effects;
            }

            internal uint Drop(uint keys, ScreenPoint point, uint effects)
            {
                _entered = false;
                Check(_target.Drop(_data, keys, point, ref effects));
                return effects;
            }

            internal DropData MarshalData()
            {
                var iid = typeof(IDataObject).GUID;
                return CoMarshalInterThreadInterfaceInStream(ref iid, _data, out var stream) >= 0
                    ? new DropData(stream) : null;
            }

            internal void Leave()
            {
                if (!_entered) return;
                _entered = false;
                _target.DragLeave();
            }

            public void Dispose()
            {
                try { Leave(); }
                finally { Marshal.ReleaseComObject(_target); }
            }
        }

        internal sealed class DropData : IDisposable
        {
            private IntPtr _stream;

            internal DropData(IntPtr stream) => _stream = stream;

            internal Task<uint> DropAsync(IntPtr owner, string target, uint enterKeys, uint dropKeys, ScreenPoint point, uint effects)
            {
                var complete = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
                var worker = new Thread(() =>
                {
                    try
                    {
                        Check(OleInitialize(IntPtr.Zero));
                        uint performed;
                        try
                        {
                            var iid = typeof(IDataObject).GUID;
                            var stream = Interlocked.Exchange(ref _stream, IntPtr.Zero);
                            Check(CoGetInterfaceAndReleaseStream(stream, ref iid, out var pointer));
                            var data = TakeInterface<IDataObject>(pointer);
                            try
                            {
                                using var session = DropSession.Create(owner, target, data);
                                // A Shell drop target can be apartment-bound. Bind it again on the worker
                                // and replay DragEnter so Windows retains the source's original drag state.
                                const uint TransferEffects = 7;
                                performed = (session.Enter(enterKeys, point, effects) & TransferEffects) != 0
                                    ? session.Drop(dropKeys, point, effects) : 0;
                            }
                            finally
                            {
                                if (Marshal.IsComObject(data)) Marshal.ReleaseComObject(data);
                            }
                        }
                        finally { OleUninitialize(); }
                        complete.SetResult(performed);
                    }
                    catch (Exception ex) { complete.SetException(ex); }
                }) { IsBackground = true, Name = "YukiDesk Windows file drop" };
                worker.SetApartmentState(ApartmentState.STA);
                worker.Start();
                return complete.Task;
            }

            public void Dispose()
            {
                var stream = Interlocked.Exchange(ref _stream, IntPtr.Zero);
                if (stream == IntPtr.Zero) return;
                CoReleaseMarshalData(stream);
                Marshal.Release(stream);
            }
        }

        internal static RecycleTotals? ReadRecycleTotals()
        {
            if (!Environment.Is64BitProcess) return null;
            var totals = new RecycleInfo { Size = (uint)Marshal.SizeOf<RecycleInfo>() };
            return SHQueryRecycleBinW(null, ref totals) == 0 && totals.Count >= 0 && totals.Bytes >= 0
                ? new RecycleTotals(totals.Count, totals.Bytes) : null;
        }

        internal static List<RecycledItem> ReadRecycleBin(CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            var items = new List<RecycledItem>();
            WithPath(RecycleBin, root =>
            {
                var folder = BindFolder(root);
                try
                {
                    const uint FoldersAndFiles = 0x60;
                    Check(folder.EnumObjects(IntPtr.Zero, FoldersAndFiles, out var enumerator));
                    if (enumerator == null) return;
                    try
                    {
                        while (true)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            int result = enumerator.Next(1, out var child, out _);
                            Check(result);
                            if (result != 0) break;
                            var absolute = ILCombine(root, child);
                            try
                            {
                                if (absolute == IntPtr.Zero || !TryGetDisplayName(folder, child, out string name))
                                    continue;
                                const uint Folder = 0x20000000;
                                uint attributes = Folder;
                                bool isFolder = folder.GetAttributesOf(1, new[] { child }, ref attributes) >= 0
                                    && (attributes & Folder) != 0;
                                var id = new byte[ILGetSize(absolute)];
                                Marshal.Copy(absolute, id, 0, id.Length);
                                var (originalFolder, deletedAt) = ReadRecycleProperties(absolute);
                                items.Add(new RecycledItem(name, id, isFolder, originalFolder, deletedAt));
                            }
                            finally
                            {
                                Marshal.FreeCoTaskMem(absolute);
                                Marshal.FreeCoTaskMem(child);
                            }
                        }
                    }
                    finally { Marshal.ReleaseComObject(enumerator); }
                }
                finally { Marshal.ReleaseComObject(folder); }
            });
            return items;
        }

        private static bool TryGetDisplayName(IShellFolder folder, IntPtr child, out string displayName)
        {
            displayName = null;
            if (folder.GetDisplayNameOf(child, 1, out var value) < 0
                || StrRetToStr(ref value, child, out var name) < 0) return false;
            try { displayName = Marshal.PtrToStringUni(name); }
            finally { Marshal.FreeCoTaskMem(name); }
            return !string.IsNullOrEmpty(displayName);
        }

        private static PropertyKey FindProperty(string name)
            => PSGetPropertyKeyFromName(name, out var key) >= 0 ? key : default;

        private static (string OriginalFolder, DateTime? DeletedAtUtc) ReadRecycleProperties(IntPtr id)
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreFromIDList(id, 0, ref iid, out var pointer) < 0) return default;
            var store = TakeInterface<IPropertyStore>(pointer);
            try
            {
                string originalFolder = ReadProperty(store, OriginalFolderKey);
                var key = DeletedAtKey;
                if (key.Format == Guid.Empty || store.GetValue(ref key, out var value) < 0)
                    return (originalFolder, null);
                try { return (originalFolder, ReadDeletedAt(value)); }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }

        private static DateTime? ReadDeletedAt(PropertyValue value)
        {
            const ushort FileTime = 64;
            if (value.Type == FileTime)
            {
                try { return value.Timestamp > 0 ? DateTime.FromFileTimeUtc(value.Timestamp) : null; }
                catch (ArgumentOutOfRangeException) { return null; }
            }
            string text = ReadPropertyText(ref value);
            return DateTimeOffset.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var date)
                ? date.UtcDateTime : null;
        }

        private static string ReadProperty(IPropertyStore store, PropertyKey key)
        {
            if (key.Format == Guid.Empty || store.GetValue(ref key, out var value) < 0) return null;
            try { return ReadPropertyText(ref value); }
            finally { PropVariantClear(ref value); }
        }

        private static string ReadPropertyText(ref PropertyValue value)
        {
            if (PropVariantToStringAlloc(ref value, out var text) < 0) return null;
            try { return Marshal.PtrToStringUni(text); }
            finally { Marshal.FreeCoTaskMem(text); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RecycleInfo
        {
            internal uint Size;
            internal long Bytes, Count;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHQueryRecycleBinW(string root, ref RecycleInfo totals);

        private static void WithPath(string path, Action<IntPtr> action)
        {
            Check(NativeMethods.SHParseDisplayName(path, IntPtr.Zero, out var id, 0, out _));
            try { action(id); }
            finally { Marshal.FreeCoTaskMem(id); }
        }

        private static void WithId(byte[] id, Action<IntPtr> action)
        {
            var handle = GCHandle.Alloc(id, GCHandleType.Pinned);
            try { action(handle.AddrOfPinnedObject()); }
            finally { handle.Free(); }
        }

        private static void WithParent(IntPtr id, Action<IShellFolder, IntPtr> action)
        {
            var iid = typeof(IShellFolder).GUID;
            Check(SHBindToParent(id, ref iid, out var pointer, out var child));
            var folder = TakeInterface<IShellFolder>(pointer);
            try { action(folder, child); }
            finally { Marshal.ReleaseComObject(folder); }
        }

        private static IShellFolder BindFolder(IntPtr id)
        {
            Check(SHGetDesktopFolder(out var desktop));
            try
            {
                var iid = typeof(IShellFolder).GUID;
                Check(desktop.BindToObject(id, IntPtr.Zero, ref iid, out var pointer));
                return TakeInterface<IShellFolder>(pointer);
            }
            finally { Marshal.ReleaseComObject(desktop); }
        }

        private static T TakeInterface<T>(IntPtr pointer) where T : class
        {
            try { return (T)Marshal.GetObjectForIUnknown(pointer); }
            finally { Marshal.Release(pointer); }
        }

        private static void Check(int result) => Marshal.ThrowExceptionForHR(result, new IntPtr(-1));

        [StructLayout(LayoutKind.Sequential)]
        internal readonly struct ScreenPoint
        {
            internal readonly int X;
            internal readonly int Y;

            internal ScreenPoint(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Command
        {
            internal int Size;
            internal uint Mask;
            internal IntPtr Owner;
            internal IntPtr Verb;
            internal IntPtr Parameters;
            internal IntPtr Directory;
            internal int Show;
            internal uint HotKey;
            internal IntPtr Icon;
        }

        private struct ShellString
        {
            internal uint Type;
            internal ShellStringValue Value;
        }

        [StructLayout(LayoutKind.Explicit, Size = 264)]
        private struct ShellStringValue
        {
            [FieldOffset(0)] internal IntPtr Wide;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            internal Guid Format;
            internal uint Id;
        }

        // PROPVARIANT 的联合体需为双指针保留空间，以便系统初始化和清理其它值类型。
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropertyValue
        {
            [FieldOffset(0)] internal ushort Type;
            [FieldOffset(8)] internal long Timestamp;
        }

        [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellFolder
        {
            [PreserveSig] int ParseDisplayName(IntPtr owner, IntPtr bind, IntPtr name, IntPtr eaten, out IntPtr id, IntPtr attributes);
            [PreserveSig] int EnumObjects(IntPtr owner, uint flags, out IEnumIdList enumerator);
            [PreserveSig] int BindToObject(IntPtr id, IntPtr bind, ref Guid iid, out IntPtr result);
            [PreserveSig] int BindToStorage(IntPtr id, IntPtr bind, ref Guid iid, out IntPtr result);
            [PreserveSig] int CompareIDs(IntPtr parameter, IntPtr first, IntPtr second);
            [PreserveSig] int CreateViewObject(IntPtr owner, ref Guid iid, out IntPtr result);
            [PreserveSig] int GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] ids, ref uint attributes);
            [PreserveSig] int GetUIObjectOf(IntPtr owner, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] ids,
                ref Guid iid, IntPtr reserved, out IntPtr result);
            [PreserveSig] int GetDisplayNameOf(IntPtr id, uint flags, out ShellString name);
            [PreserveSig] int SetNameOf(IntPtr owner, IntPtr id, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr renamed);
        }

        [ComImport, Guid("000214F2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumIdList
        {
            [PreserveSig] int Next(uint count, out IntPtr id, out uint fetched);
        }

        [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
            [PreserveSig] int InvokeCommand(ref Command invocation);
        }

        [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDropTarget
        {
            [PreserveSig] int DragEnter(IDataObject data, uint keys, ScreenPoint point, ref uint effects);
            [PreserveSig] int DragOver(uint keys, ScreenPoint point, ref uint effects);
            [PreserveSig] int DragLeave();
            [PreserveSig] int Drop(IDataObject data, uint keys, ScreenPoint point, ref uint effects);
        }

        [ComImport, Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDataObjectAsyncCapability
        {
            [PreserveSig] int SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool enabled);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropertyValue value);
        }

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHBindToParent(IntPtr id, ref Guid iid, out IntPtr parent, out IntPtr child);
        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHGetDesktopFolder(out IShellFolder folder);
        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern IntPtr ILCombine(IntPtr first, IntPtr second);
        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern uint ILGetSize(IntPtr id);
        [DllImport("shlwapi.dll", EntryPoint = "StrRetToStrW", ExactSpelling = true)]
        private static extern int StrRetToStr(ref ShellString value, IntPtr id, out IntPtr name);
        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHGetPropertyStoreFromIDList(IntPtr id, uint flags, ref Guid iid, out IntPtr store);
        [DllImport("propsys.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int PSGetPropertyKeyFromName(string name, out PropertyKey key);
        [DllImport("propsys.dll", ExactSpelling = true)]
        private static extern int PropVariantToStringAlloc(ref PropertyValue value, out IntPtr text);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int PropVariantClear(ref PropertyValue value);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int OleInitialize(IntPtr reserved);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void OleUninitialize();
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int RegisterDragDrop(IntPtr window, IDropTarget target);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int RevokeDragDrop(IntPtr window);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoMarshalInterThreadInterfaceInStream(ref Guid iid, IDataObject data, out IntPtr stream);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoGetInterfaceAndReleaseStream(IntPtr stream, ref Guid iid, out IntPtr data);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoReleaseMarshalData(IntPtr stream);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHEmptyRecycleBinW")]
        private static extern int SHEmptyRecycleBin(IntPtr owner, string root, uint flags);
        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHDoDragDrop(IntPtr owner, IntPtr data, IntPtr source, uint effects, out uint performed);
        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr menu);
    }
}

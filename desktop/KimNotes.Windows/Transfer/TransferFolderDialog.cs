using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace KimNotes.Windows.Transfer
{
    internal static class TransferFolderDialog
    {
        private const int Cancelled = unchecked((int)0x800704C7);
        private const uint NoChangeDirectory = 0x00000008;
        private const uint PickFolders = 0x00000020;
        private const uint ForceFileSystem = 0x00000040;
        private const uint PathMustExist = 0x00000800;
        private const uint FileSystemPath = 0x80058000;

        internal static string DownloadsFolder()
        {
            var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            int result = SHGetKnownFolderPath(ref downloads, 0, IntPtr.Zero, out IntPtr path);
            try { return result < 0 ? null : Marshal.PtrToStringUni(path); }
            finally { Marshal.FreeCoTaskMem(path); }
        }

        internal static string Choose(Window owner, string initialFolder, CancellationToken token, bool sending = false)
        {
            if (token.IsCancellationRequested) return null;

            var dialog = (IFileDialog)(object)new FileOpenDialog();
            try
            {
                Marshal.ThrowExceptionForHR(dialog.GetOptions(out uint options));
                Marshal.ThrowExceptionForHR(dialog.SetOptions(options | NoChangeDirectory | PickFolders | ForceFileSystem | PathMustExist));
                Marshal.ThrowExceptionForHR(dialog.SetTitle(sending ? "选择要发送的文件夹" : "选择保存位置"));
                Marshal.ThrowExceptionForHR(dialog.SetOkButtonLabel("选择此文件夹"));
                ApplyStartingFolder(dialog, initialFolder);

                int result = ShowOnOwner(dialog, owner, token);
                if (result == Cancelled || token.IsCancellationRequested) return null;
                Marshal.ThrowExceptionForHR(result);
                Marshal.ThrowExceptionForHR(dialog.GetResult(out IShellItem folder));
                try
                {
                    Marshal.ThrowExceptionForHR(folder.GetDisplayName(FileSystemPath, out IntPtr path));
                    try { return Marshal.PtrToStringUni(path); }
                    finally { Marshal.FreeCoTaskMem(path); }
                }
                finally { Marshal.ReleaseComObject(folder); }
            }
            finally { Marshal.ReleaseComObject(dialog); }
        }

        private static int ShowOnOwner(IFileDialog dialog, Window owner, CancellationToken token)
        {
            var dispatcher = owner.Dispatcher;
            var handle = new WindowInteropHelper(owner).EnsureHandle();
            bool showing = false;
            using (token.Register(() =>
            {
                if (dispatcher.HasShutdownStarted) return;
                dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() =>
                {
                    if (showing) dialog.Close(Cancelled);
                }));
            }))
            {
                if (token.IsCancellationRequested) return Cancelled;
                showing = true;
                try { return dialog.Show(handle); }
                finally { showing = false; }
            }
        }
        private static void ApplyStartingFolder(IFileDialog dialog, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            Guid itemId = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out IShellItem folder) < 0 || folder == null) return;
            try { dialog.SetDefaultFolder(folder); }
            finally { Marshal.ReleaseComObject(folder); }
        }

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr binding,
            ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"), ClassInterface(ClassInterfaceType.None)]
        private sealed class FileOpenDialog { }

        // Preserve vtable order from IModalWindow::Show through IFileDialog::Close.
        [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            [PreserveSig] int SetFileTypes(uint count, IntPtr filters);
            [PreserveSig] int SetFileTypeIndex(uint index);
            [PreserveSig] int GetFileTypeIndex(out uint index);
            [PreserveSig] int Advise(IntPtr events, out uint cookie);
            [PreserveSig] int Unadvise(uint cookie);
            [PreserveSig] int SetOptions(uint options);
            [PreserveSig] int GetOptions(out uint options);
            [PreserveSig] int SetDefaultFolder(IShellItem folder);
            [PreserveSig] int SetFolder(IShellItem folder);
            [PreserveSig] int GetFolder(out IShellItem folder);
            [PreserveSig] int GetCurrentSelection(out IShellItem item);
            [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int GetFileName(out IntPtr name);
            [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            [PreserveSig] int GetResult(out IShellItem item);
            [PreserveSig] int AddPlace(IShellItem item, uint placement);
            [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            [PreserveSig] int Close(int result);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr binding, ref Guid handler, ref Guid iid, out IntPtr result);
            [PreserveSig] int GetParent(out IShellItem parent);
            [PreserveSig] int GetDisplayName(uint format, out IntPtr name);
        }
    }
}

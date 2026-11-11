using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace VideoViewer.Services
{
    /// <summary>Modern Windows folder picker (multi-select) and Explorer thumbnail access.</summary>
    internal static class Native
    {
        // ---------------- Folder picker ----------------
        const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_ALLOWMULTISELECT = 0x200, FOS_PATHMUSTEXIST = 0x800;
        const uint SIGDN_FILESYSPATH = 0x80058000;

        public static List<string> PickFolders(Window owner, string title)
        {
            var result = new List<string>();
            var dlg = (IFileOpenDialog)new FileOpenDialogRCW();
            try
            {
                dlg.GetOptions(out uint opts);
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_ALLOWMULTISELECT | FOS_PATHMUSTEXIST);
                dlg.SetTitle(title);
                dlg.SetOkButtonLabel("Add folder");
                var hwnd = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;
                if (dlg.Show(hwnd) != 0) return result; // cancelled
                dlg.GetResults(out IShellItemArray items);
                items.GetCount(out uint n);
                for (uint i = 0; i < n; i++)
                {
                    items.GetItemAt(i, out IShellItem it);
                    it.GetDisplayName(SIGDN_FILESYSPATH, out string path);
                    if (!string.IsNullOrEmpty(path)) result.Add(path);
                    Marshal.ReleaseComObject(it);
                }
                Marshal.ReleaseComObject(items);
            }
            catch { }
            finally { Marshal.ReleaseComObject(dlg); }
            return result;
        }

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogRCW { }

        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
            void GetResults(out IShellItemArray ppenum);
            void GetSelectedItems(out IShellItemArray ppsai);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemArray
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
            void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
            void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
            void GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
            void GetCount(out uint pdwNumItems);
            void GetItemAt(uint dwIndex, out IShellItem ppsi);
            void EnumItems(out IntPtr ppenumShellItems);
        }

        // ---------------- Explorer thumbnails ----------------
        [StructLayout(LayoutKind.Sequential)]
        struct NativeSize { public int cx, cy; }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, [In] ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);

        const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_THUMBNAILONLY = 0x8;

        /// <summary>Asks Windows for a real thumbnail (never a generic icon). Null if Windows can't make one.</summary>
        public static BitmapSource ShellThumbnail(string path, int size)
        {
            IShellItemImageFactory f = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                var iid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out f);
                int hr = f.GetImage(new NativeSize { cx = size, cy = size }, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, out hbm);
                if (hr != 0 || hbm == IntPtr.Zero) return null;
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally
            {
                if (hbm != IntPtr.Zero) DeleteObject(hbm);
                if (f != null) Marshal.ReleaseComObject(f);
            }
        }
    }
}

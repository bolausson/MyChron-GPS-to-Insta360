using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MyChron2Insta360.App;

/// <summary>
/// The pickers behind "Add folder…" and "Add file(s)…", driven through the shell's own open dialog
/// rather than WinForms' wrappers around it. Windows can start that dialog in two ways: in a default
/// folder it uses only until the user has picked something, after which it reopens where they last
/// picked from, or in a folder it opens every time. WinForms' InitialDirectory always does the second,
/// so the first, which is what an unticked "always open this path" means, is out of its reach.
/// </summary>
internal static class SourcePicker
{
    /// <summary>
    /// Windows remembers where a dialog was last per program, or per id when it is given one. This id
    /// keeps the Sources pickers' memory apart from the program's other dialogs, so browsing for the
    /// output folder does not move where they open next.
    /// </summary>
    private static readonly Guid Memory = new("0c809cb3-aff7-4422-a1a9-376fb835767d");

    /// <summary>
    /// The folder spelled the way the shell accepts it, or null when it is not a full path to an
    /// existing folder. .NET also takes "C:/data" or "data\..\data", which the shell refuses.
    /// </summary>
    public static string? UsableFolder(string dir) =>
        Path.IsPathFullyQualified(dir) && Directory.Exists(dir) ? Path.GetFullPath(dir) : null;

    /// <summary>One or more files, or none when the user cancels.</summary>
    public static string[] PickFiles(IWin32Window owner, IReadOnlyList<(string Name, string Pattern)> filters,
                                     string? start, bool always) =>
        Show(owner, start, always, FOS_ALLOWMULTISELECT, dialog =>
        {
            var specs = filters.Select(f => new FilterSpec { Name = f.Name, Pattern = f.Pattern }).ToArray();
            dialog.SetFileTypes((uint)specs.Length, specs);
        });

    /// <summary>One folder, or null when the user cancels.</summary>
    public static string? PickFolder(IWin32Window owner, string description, string? start, bool always) =>
        Show(owner, start, always, FOS_PICKFOLDERS, dialog =>
        {
            // The description is a nicety, so a dialog without custom controls simply goes without it.
            if (dialog is IFileDialogCustomize custom) custom.AddText(0, description);
        }).FirstOrDefault();

    /// <param name="start">Existing folder to open in, spelled out in full as the shell wants it, or null
    /// to leave the choice to Windows.</param>
    /// <param name="always">Open there even when Windows remembers a folder from an earlier pick.</param>
    private static string[] Show(IWin32Window owner, string? start, bool always, uint mode, Action<IFileOpenDialog> setUp)
    {
        Application.OleRequired();   // drag and drop inside the dialog needs OLE, as WinForms' dialogs ensure
        var dialog = (IFileOpenDialog)new FileOpenDialog();
        try
        {
            Guid memory = Memory;
            dialog.SetClientGuid(ref memory);   // before anything else, as the documentation asks
            dialog.GetOptions(out uint options);
            dialog.SetOptions(options | mode | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR);
            setUp(dialog);

            if (start is not null && ShellItem(start) is { } folder)
            {
                dialog.SetDefaultFolder(folder);         // until the user has picked something
                if (always) dialog.SetFolder(folder);    // and even after they have
                Marshal.ReleaseComObject(folder);
            }

            int hr = dialog.Show(owner.Handle);
            if (hr == ErrorCancelled) return Array.Empty<string>();
            Marshal.ThrowExceptionForHR(hr);

            dialog.GetResults(out IShellItemArray picked);
            try
            {
                picked.GetCount(out uint count);
                var paths = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    picked.GetItemAt(i, out IShellItem item);
                    paths[i] = PathOf(item);
                    Marshal.ReleaseComObject(item);
                }
                return paths;
            }
            finally { Marshal.ReleaseComObject(picked); }
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    private static IShellItem? ShellItem(string path)
    {
        Guid iid = typeof(IShellItem).GUID;
        return SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItem item) >= 0 ? item : null;
    }

    private static string PathOf(IShellItem item)
    {
        item.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr name);
        try { return Marshal.PtrToStringUni(name)!; }
        finally { Marshal.FreeCoTaskMem(name); }
    }

    private const uint FOS_NOCHANGEDIR = 0x8, FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40,
                       FOS_ALLOWMULTISELECT = 0x200;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int ErrorCancelled = unchecked((int)0x800704C7);   // HRESULT_FROM_WIN32(ERROR_CANCELLED)

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItem item);

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FilterSpec
    {
        public string Name;
        public string Pattern;
    }

    // What has to match the shell is the order: every method up to the last one called is declared, in
    // the order of ShObjIdl_core.h, the unused ones included, because the position picks the method.

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow. Cancelling returns an error code rather than throwing.
        [PreserveSig] int Show(IntPtr owner);

        // IFileDialog
        void SetFileTypes(uint count, [In, MarshalAs(UnmanagedType.LPArray)] FilterSpec[] specs);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int where);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);

        // IFileOpenDialog
        void GetResults(out IShellItemArray items);
        void GetSelectedItems(out IShellItemArray items);
    }

    [ComImport, Guid("e6fdd21a-163f-4975-9c8c-a69f1ba37034"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogCustomize
    {
        void EnableOpenDropDown(uint id);
        void AddMenu(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label);
        void AddPushButton(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label);
        void AddComboBox(uint id);
        void AddRadioButtonList(uint id);
        void AddCheckButton(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label, [MarshalAs(UnmanagedType.Bool)] bool isChecked);
        void AddEditBox(uint id, [MarshalAs(UnmanagedType.LPWStr)] string text);
        void AddSeparator(uint id);
        void AddText(uint id, [MarshalAs(UnmanagedType.LPWStr)] string text);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetPropertyStore(int flags, ref Guid iid, out IntPtr store);
        void GetPropertyDescriptionList(IntPtr keyType, ref Guid iid, out IntPtr list);
        void GetAttributes(int flags, uint mask, out uint attributes);
        void GetCount(out uint count);
        void GetItemAt(uint index, out IShellItem item);
        void EnumItems(out IntPtr items);
    }
}

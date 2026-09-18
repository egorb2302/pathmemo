using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PathMemo.Platform.Native;

/// <summary>
/// The shell's own file operation object, used for one thing only: putting items in the
/// Recycle Bin the way Explorer does (README section 9.6).
/// </summary>
/// <remarks>
/// <para>
/// <c>SHFileOperation</c> was rejected: it is deprecated, it swallows errors, and it
/// returns success after doing nothing. <c>IFileOperation</c> reports per item, through a
/// sink, which is the only way to learn what actually happened.
/// </para>
/// <para>
/// Declared with <see cref="GeneratedComInterfaceAttribute"/> rather than
/// <c>ComImport</c>: the source-generated marshalling has no reflection in it, so the
/// NativeAOT and trimming path stays open (README section 19.4). The method order is the
/// vtable order and must not be rearranged - every entry is here, including the ones this
/// application never calls, because the slots have to line up.
/// </para>
/// </remarks>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem
{
    void BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);
    void GetParent(out IShellItem ppsi);
    void GetDisplayName(uint sigdnName, out nint ppszName);
    void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    void Compare(IShellItem psi, uint hint, out int piOrder);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
internal partial interface IFileOperationProgressSink
{
    void StartOperations();
    void FinishOperations(int hrResult);
    void PreRenameItem(uint dwFlags, IShellItem psiItem, string? pszNewName);
    void PostRenameItem(uint dwFlags, IShellItem psiItem, string? pszNewName, int hrRename, IShellItem? psiNewlyCreated);
    void PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName);
    void PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrMove, IShellItem? psiNewlyCreated);
    void PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName);
    void PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated);
    void PreDeleteItem(uint dwFlags, IShellItem psiItem);
    void PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated);
    void PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, string? pszNewName);
    void PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, string? pszNewName, string? pszTemplateName,
        uint dwFileAttributes, int hrNew, IShellItem? psiNewItem);
    void UpdateProgress(uint iWorkTotal, uint iWorkSoFar);
    void ResetTimer();
    void PauseTimer();
    void ResumeTimer();
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
internal partial interface IFileOperation
{
    void Advise(IFileOperationProgressSink pfops, out uint pdwCookie);
    void Unadvise(uint dwCookie);
    void SetOperationFlags(uint dwOperationFlags);
    void SetProgressMessage(string pszMessage);
    void SetProgressDialog(nint popd);
    void SetProperties(nint pproparray);
    void SetOwnerWindow(nint hwndOwner);
    void ApplyPropertiesToItem(IShellItem psiItem);
    void ApplyPropertiesToItems(nint punkItems);
    void RenameItem(IShellItem psiItem, string pszNewName, IFileOperationProgressSink? pfopsItem);
    void RenameItems(nint pUnkItems, string pszNewName);
    void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, IFileOperationProgressSink? pfopsItem);
    void MoveItems(nint punkItems, IShellItem psiDestinationFolder);
    void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, string? pszCopyName, IFileOperationProgressSink? pfopsItem);
    void CopyItems(nint punkItems, IShellItem psiDestinationFolder);
    void DeleteItem(IShellItem psiItem, IFileOperationProgressSink? pfopsItem);
    void DeleteItems(nint punkItems);
    void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, string? pszName, string? pszTemplateName,
        IFileOperationProgressSink? pfopsItem);
    void PerformOperations();
    void GetAnyOperationsAborted(out int pfAnyOperationsAborted);
}

internal static partial class ShellOperations
{
    /// <summary>CLSID_FileOperation.</summary>
    internal static readonly Guid FileOperationClsid = new("3ad05575-8857-4850-9277-11b85bdb8e09");

    internal static readonly Guid FileOperationIid = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
    internal static readonly Guid ShellItemIid = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    internal const uint ClsctxInprocServer = 0x1;

    // FOF_* / FOFX_* flags used in README section 9.6.
    internal const uint FofSilent = 0x0004;
    internal const uint FofNoConfirmation = 0x0010;
    internal const uint FofNoErrorUi = 0x0400;
    internal const uint FofWantNukeWarning = 0x4000;
    internal const uint FofxRecycleOnDelete = 0x00080000;

    [LibraryImport("ole32.dll", EntryPoint = "CoCreateInstance")]
    internal static partial int CoCreateInstance(
        in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    [LibraryImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHCreateItemFromParsingName(
        string pszPath, nint pbc, in Guid riid, out nint ppv);
}

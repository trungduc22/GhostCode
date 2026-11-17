using Windows.Storage.Pickers;
using WinRT.Interop;

namespace GhostCode.App.Interop;

/// <summary>
/// Shared WinUI 3 file/folder picker helpers initialized with the main HWND.
/// </summary>
internal static class FilePickerHelpers
{
    public static async Task<string?> PickSingleFolderAsync(
        IntPtr hwnd,
        PickerLocationId startLocation = PickerLocationId.DocumentsLibrary)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = startLocation
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public static async Task<string?> PickExecutableAsync(IntPtr hwnd)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}

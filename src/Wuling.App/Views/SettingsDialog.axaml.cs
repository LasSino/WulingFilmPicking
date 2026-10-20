using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Wuling.Core.Commit;

namespace Wuling.App.Views;

/// <summary>处理设置对话框(03 F8): 三类标记各自动作 + RAW/JPEG 绑定。</summary>
public partial class SettingsDialog : Window
{
    public CommitOptions Result { get; private set; } = new();

    public SettingsDialog()
    {
        InitializeComponent();
    }

    public SettingsDialog(CommitOptions current) : this()
    {
        SelNone.IsChecked = current.OnSelected.Kind == CommitActionKind.None;
        SelMove.IsChecked = current.OnSelected.Kind == CommitActionKind.Move;
        SelTarget.Text = current.OnSelected.TargetDirectory;

        RejSoft.IsChecked = current.OnRejected.Kind == CommitActionKind.SoftDelete;
        RejDelete.IsChecked = current.OnRejected.Kind == CommitActionKind.Delete;
        RejNone.IsChecked = current.OnRejected.Kind == CommitActionKind.None;

        UnmNone.IsChecked = current.OnUnmarked.Kind == CommitActionKind.None;
        UnmMove.IsChecked = current.OnUnmarked.Kind == CommitActionKind.Move;
        UnmTarget.Text = current.OnUnmarked.TargetDirectory;

        KeepPairs.IsChecked = current.KeepPairsTogether;
    }

    private async void OnPickSelTarget(object? sender, RoutedEventArgs e) => await PickDirAsync(SelTarget);

    private async void OnPickUnmTarget(object? sender, RoutedEventArgs e) => await PickDirAsync(UnmTarget);

    private async Task PickDirAsync(TextBox target)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择目标目录",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].Path is { IsAbsoluteUri: true, Scheme: "file" } uri)
        {
            target.Text = uri.LocalPath;
        }
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        Result = new CommitOptions
        {
            OnSelected = SelMove.IsChecked == true
                ? new TagAction { Kind = CommitActionKind.Move, TargetDirectory = SelTarget.Text }
                : TagAction.None,
            OnRejected = RejDelete.IsChecked == true
                ? new TagAction { Kind = CommitActionKind.Delete }
                : RejNone.IsChecked == true ? TagAction.None : new TagAction { Kind = CommitActionKind.SoftDelete },
            OnUnmarked = UnmMove.IsChecked == true
                ? new TagAction { Kind = CommitActionKind.Move, TargetDirectory = UnmTarget.Text }
                : TagAction.None,
            KeepPairsTogether = KeepPairs.IsChecked == true,
        };

        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}

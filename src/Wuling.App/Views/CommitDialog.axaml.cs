using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Wuling.Core.Commit;
using Wuling.Core.Models;

namespace Wuling.App.Views;

/// <summary>提交确认对话框: 汇总计划 → 执行 → 报告(01 §2.4)。</summary>
public partial class CommitDialog : Window
{
    private readonly CommitPlan _plan;
    private readonly string _rootPath;
    public CommitResult? Result { get; private set; }

    public CommitDialog()
    {
        InitializeComponent();
        _plan = new CommitPlan([]);
        _rootPath = string.Empty;
    }

    public CommitDialog(CommitPlan plan, string rootPath)
    {
        InitializeComponent();
        _plan = plan;
        _rootPath = rootPath;
        SummaryBox.Text = Describe(plan, "待执行");
    }

    private static string Describe(CommitPlan plan, string title)
    {
        if (plan.Count == 0)
        {
            return "(没有任何待执行操作 — 没有照片需要处置)";
        }

        return $"{title}: 共 {plan.Count} 个文件\n" + string.Join(
            Environment.NewLine,
            plan.Items.Select(i =>
            {
                var action = i.Action.Kind switch
                {
                    CommitActionKind.SoftDelete => "软删除",
                    CommitActionKind.Delete => "永久删除 ⚠",
                    CommitActionKind.Move => $"移动 → {i.Action.TargetDirectory}",
                    _ => "无动作",
                };
                return $"{action,-30} {Path.GetFileName(i.FilePath)}";
            }).Distinct());
    }

    private async void OnExecute(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OkButton.IsEnabled = false;
        Progress.IsVisible = true;
        var executor = new CommitExecutor();
        Result = await System.Threading.Tasks.Task.Run(() => executor.Execute(_plan, _rootPath));
        Progress.IsVisible = false;
        SummaryBox.Text = DescribeDone(Result);
        OkButton.Content = "关闭";
        OkButton.IsEnabled = true;
        OkButton.Click -= OnExecute;
        OkButton.Click += (_, _) => Close();
    }

    private static string DescribeDone(CommitResult result) =>
        $"执行完成: 成功 {result.SuccessCount},失败 {result.FailureCount}\n" +
        string.Join(
            Environment.NewLine,
            result.Results.Where(r => !r.Success).Select(r => $"失败: {Path.GetFileName(r.Item.FilePath)} — {r.Error}"));

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}

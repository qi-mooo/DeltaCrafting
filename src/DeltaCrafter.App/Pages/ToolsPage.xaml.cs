using DeltaCrafter.App.Services;
using DeltaCrafter.Core.L0;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DeltaCrafter.App.Pages;

public sealed partial class ToolsPage : Page
{
    private readonly AppHost _host = AppHost.Current;
    private DataToolQuery? _query;
    private DataToolResult? _result;
    private CancellationTokenSource? _request;
    private int _generation;

    public ToolsPage() => InitializeComponent(); // 菜单不预取数据。

    private async void OpenTool(object sender, RoutedEventArgs e)
    {
        _query = new((string)((Button)sender).Tag);
        ToolMenu.Visibility = Visibility.Collapsed;
        ToolContent.Visibility = Visibility.Visible;
        ToolTitle.Text = _query.Title;
        GunFilters.Visibility = Paging.Visibility = _query.Tool == "gun" ? Visibility.Visible : Visibility.Collapsed;
        GunMode.SelectedIndex = 0;
        GunSearch.Text = "";
        await Fetch();
    }

    private async Task Fetch()
    {
        if (_query is null) return;
        int generation = ++_generation;
        _request?.Cancel();
        _request?.Dispose();
        _request = CancellationTokenSource.CreateLinkedTokenSource(_host.AppStopToken);
        Loading.IsActive = true;
        RefreshButton.IsEnabled = SearchButton.IsEnabled = PreviousButton.IsEnabled = NextButton.IsEnabled = false;
        ToolStatus.Text = "正在查询…";
        Results.ItemsSource = null;
        _result = null;
        try
        {
            var result = await _host.DataTools.FetchAsync(_query, _host.Settings.ManufactureApi.Token, _request.Token);
            if (generation != _generation) return;
            _result = result;
            Results.ItemsSource = result.Entries;
            ToolStatus.Text = result.Detail + $" · 更新于 {result.FetchedAt:HH:mm:ss}" + (result.Entries.Count == 0 ? " · 暂无结果" : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == _generation) ToolStatus.Text = ex.Message; }
        finally
        {
            if (generation == _generation)
            {
                Loading.IsActive = false;
                RefreshButton.IsEnabled = SearchButton.IsEnabled = true;
                PreviousButton.IsEnabled = _query.Page > 1;
                NextButton.IsEnabled = _result?.HasNext == true;
            }
        }
    }

    private void CancelQuery() { ++_generation; _request?.Cancel(); Loading.IsActive = false; }
    private void BackToTools(object sender, RoutedEventArgs e)
    {
        CancelQuery();
        _query = null;
        ToolContent.Visibility = Visibility.Collapsed;
        ToolMenu.Visibility = Visibility.Visible;
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e) { CancelQuery(); base.OnNavigatedFrom(e); }
    private async void RefreshTool(object sender, RoutedEventArgs e) => await Fetch();
    private async void SearchGuns(object sender, RoutedEventArgs e)
    {
        _query = new("gun", 1, GunMode.SelectedIndex == 1 ? "operator" : "gun", GunSearch.Text.Trim());
        await Fetch();
    }
    private async void PreviousPage(object sender, RoutedEventArgs e)
    { if (_query is { Page: > 1 }) { _query = _query with { Page = _query.Page - 1 }; await Fetch(); } }
    private async void NextPage(object sender, RoutedEventArgs e)
    { if (_query is not null && _result?.HasNext == true) { _query = _query with { Page = _query.Page + 1 }; await Fetch(); } }
    private void CopyButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            button.Visibility = button.DataContext is DataToolEntry { CopyText.Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }
    private void CopyCode(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is DataToolEntry entry)
            ToolStatus.Text = _host.DataTools.CopyGunCode(entry.CopyText).Message;
    }
}

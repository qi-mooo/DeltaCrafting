using DeltaCrafter.App.Services;
using DeltaCrafter.Core.L0;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System.Runtime.InteropServices.WindowsRuntime;

namespace DeltaCrafter.App.Pages;

public sealed partial class ToolsPage : Page
{
    private readonly AppHost _host = AppHost.Current;
    private DataToolQuery? _query;
    private DataToolResult? _result, _weapons;
    private DataToolEntry? _entry;
    private CancellationTokenSource? _request;
    private int _generation;
    private string _mode = "gun", _screen = "menu";
    private string GunTitle => "改枪码·" + (_mode == "operator" ? "大战场" : "烽火地带");

    public ToolsPage() => InitializeComponent();
    private void Show(string screen)
    {
        _screen = screen;
        ToolMenu.Visibility = screen == "menu" ? Visibility.Visible : Visibility.Collapsed;
        ModeMenu.Visibility = screen == "mode" ? Visibility.Visible : Visibility.Collapsed;
        QueryMenu.Visibility = screen == "query" ? Visibility.Visible : Visibility.Collapsed;
        Results.Visibility = screen is "list" or "weapons" ? Visibility.Visible : Visibility.Collapsed;
        PasswordDetail.Visibility = screen == "password" ? Visibility.Visible : Visibility.Collapsed;
        MarketDetail.Visibility = screen == "market" ? Visibility.Visible : Visibility.Collapsed;
        GunDetail.Visibility = screen == "gun" ? Visibility.Visible : Visibility.Collapsed;
        Toolbar.Visibility = screen is "menu" or "market" ? Visibility.Collapsed : Visibility.Visible;
        bool paging = screen == "list" && _query?.Tool == "gun";
        PreviousButton.Visibility = NextButton.Visibility = paging ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.Visibility = screen == "list" && _query?.Tool != "market" ? Visibility.Visible : Visibility.Collapsed;
        PageNumber.Text = paging ? $"{_query!.Page} / {_result?.TotalPages ?? _query.Page}" : "";
        ToolStatus.Text = "";
        ToolTitle.Text = screen == "menu" ? "工具" : screen == "mode" ? "改枪码" : screen is "query" or "weapons" ? GunTitle : ToolTitle.Text;
    }
    private async void OpenTool(object sender, RoutedEventArgs e)
    {
        CancelQuery();
        string tool = (string)((Button)sender).Tag;
        if (tool == "gun") { Show("mode"); return; }
        _query = new(tool);
        await Fetch();
    }
    private void ChooseMode(object sender, RoutedEventArgs e)
    { _mode = (string)((Button)sender).Tag; Show("query"); }
    private async void PopularGuns(object sender, RoutedEventArgs e)
    { _query = new("gun", Mode: _mode); await Fetch(); }
    private async void ChooseWeapon(object sender, RoutedEventArgs e)
    {
        _query = new("gun-keys", Mode: _mode);
        await Fetch();
    }
    private async Task Fetch()
    {
        if (_query is null) return;
        var query = _query;
        CancelQuery();
        int generation = _generation;
        _request = CancellationTokenSource.CreateLinkedTokenSource(_host.AppStopToken);
        Show(query.Tool == "gun-keys" ? "weapons" : "list");
        ToolTitle.Text = query.Tool is "gun" or "gun-keys" ? GunTitle : query.Title;
        Loading.IsActive = true;
        RefreshButton.IsEnabled = PreviousButton.IsEnabled = NextButton.IsEnabled = false;
        ToolStatus.Text = "正在查询…";
        Results.ItemsSource = null;
        _result = null;
        try
        {
            var result = await _host.DataTools.FetchAsync(query, _host.Settings.ManufactureApi.Token, _request.Token);
            if (generation != _generation) return;
            _result = result;
            if (query.Tool == "gun-keys") _weapons = result;
            Results.ItemsSource = result.Entries;
            ToolStatus.Text = result.Tool == "market" ? result.Detail : result.Entries.Count == 0 ? "暂无结果" : "";
            PageNumber.Text = query.Tool == "gun" ? $"{query.Page} / {result.TotalPages}" : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == _generation) ToolStatus.Text = ex.Message; }
        finally
        {
            if (generation == _generation)
            {
                Loading.IsActive = false;
                RefreshButton.IsEnabled = true;
                PreviousButton.IsEnabled = query.Page > 1;
                NextButton.IsEnabled = _result?.HasNext == true;
                // A failed initial market request can be retried; valid data is always served from its expiry cache.
                if (query.Tool == "market" && _result is null) RefreshButton.Visibility = Visibility.Visible;
            }
        }
    }
    private async void OpenEntry(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not DataToolEntry entry || _query is null) return;
        if (_screen == "weapons")
        {
            _query = new("gun", Mode: _mode, Category: entry.Category, Weapon: entry.Title);
            await Fetch(); return;
        }
        _entry = entry;
        Show(_query.Tool);
        if (_query.Tool == "password")
        {
            ToolTitle.Text = entry.Title;
            PasswordText.Text = entry.Password;
            ToolStatus.Text = entry.Date;
        }
        else if (_query.Tool == "market")
        {
            ToolTitle.Text = "价格  " + entry.Price;
            MarketName.Text = entry.Title;
            ToolStatus.Text = _result?.Detail ?? "";
            MarketImage.Source = null;
            ImageStatus.Text = "正在加载图片…";
            int generation = _generation;
            try
            {
                var image = await _host.DataTools.ImageAsync(entry.Id, _host.AppStopToken);
                if (generation != _generation || _entry != entry || _screen != "market") return;
                var rgb = Convert.FromBase64String(image.Pixels);
                byte[] bgra = new byte[image.Width * image.Height * 4];
                for (int i = 0; i < rgb.Length / 2; i++)
                {
                    int color = rgb[i * 2] | rgb[i * 2 + 1] << 8;
                    bgra[i * 4] = (byte)((color & 31) * 255 / 31);
                    bgra[i * 4 + 1] = (byte)((color >> 5 & 63) * 255 / 63);
                    bgra[i * 4 + 2] = (byte)((color >> 11) * 255 / 31);
                    bgra[i * 4 + 3] = 255;
                }
                var bitmap = new WriteableBitmap(image.Width, image.Height);
                using (var stream = bitmap.PixelBuffer.AsStream()) await stream.WriteAsync(bgra);
                bitmap.Invalidate();
                MarketImage.Source = bitmap;
                ImageStatus.Text = "";
            }
            catch (Exception) { if (generation == _generation && _screen == "market") ImageStatus.Text = "图片暂不可用，返回后可重试"; }
        }
        else
        {
            ToolTitle.Text = GunTitle;
            GunName.Text = entry.ListTitle;
            GunText.Text = entry.Detail;
        }
    }
    private void CancelQuery() { ++_generation; _request?.Cancel(); _request?.Dispose(); _request = null; Loading.IsActive = false; }
    private void Back(object sender, RoutedEventArgs e)
    {
        string screen = _screen;
        CancelQuery();
        if (screen is "password" or "market" or "gun")
        {
            Show("list");
            ToolTitle.Text = _query!.Tool == "gun" ? GunTitle : _query.Title;
            Results.ItemsSource = _result?.Entries;
            ToolStatus.Text = _query.Tool == "market" ? _result?.Detail ?? "" : "";
        }
        else if (screen == "list" && _query?.Tool == "gun" && _query.Weapon != "全部" && _weapons is not null)
        { Show("weapons"); Results.ItemsSource = _weapons.Entries; }
        else if (screen == "weapons" || screen == "list" && _query?.Tool == "gun") Show("query");
        else if (screen == "query") Show("mode");
        else Show("menu");
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e) { CancelQuery(); base.OnNavigatedFrom(e); }
    private async void RefreshTool(object sender, RoutedEventArgs e) => await Fetch();
    private async void PreviousPage(object sender, RoutedEventArgs e)
    { if (_query is { Page: > 1 }) { _query = _query with { Page = _query.Page - 1 }; await Fetch(); } }
    private async void NextPage(object sender, RoutedEventArgs e)
    { if (_query is not null && _result?.HasNext == true) { _query = _query with { Page = _query.Page + 1 }; await Fetch(); } }
    private void CopyCode(object sender, RoutedEventArgs e)
    { if (_entry is not null) ToolStatus.Text = _host.DataTools.CopyGunCode(_entry.CopyText).Message; }
}

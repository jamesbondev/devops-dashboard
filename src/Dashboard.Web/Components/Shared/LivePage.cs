using Dashboard.Web.Services;
using Dashboard.Web.Sync;
using Microsoft.AspNetCore.Components;

namespace Dashboard.Web.Components.Shared;

/// <summary>Base for pages that reload when a sync pass finishes and re-render periodically for relative times.</summary>
public abstract class LivePage : ComponentBase, IDisposable
{
    [Inject] protected SyncSignals Signals { get; set; } = default!;
    [Inject] protected IClock Clock { get; set; } = default!;
    [Inject] protected ConfigService Config { get; set; } = default!;
    private Timer? _timer;
    private long _seenVersion = -1;
    protected bool Loading { get; private set; } = true;
    protected string? LoadError { get; private set; }
    protected DateTimeOffset Now => Clock.UtcNow;

    protected abstract Task LoadAsync();
    protected virtual TimeSpan RefreshEvery => TimeSpan.FromSeconds(30);

    protected override async Task OnInitializedAsync()
    {
        Signals.DataChanged += OnChanged;
        _timer = new Timer(_ => _ = InvokeAsync(async () => { await SafeLoadAsync(); StateHasChanged(); }), null, RefreshEvery, RefreshEvery);
        await SafeLoadAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_seenVersion >= 0) await SafeLoadAsync();
    }

    protected async Task SafeLoadAsync()
    {
        try
        {
            _seenVersion = Signals.Version;
            await LoadAsync();
            LoadError = null;
        }
        catch (Exception ex) { LoadError = ex.Message; }
        finally { Loading = false; }
    }

    private void OnChanged()
    {
        if (Signals.Version == _seenVersion) return;
        _ = InvokeAsync(async () => { await SafeLoadAsync(); StateHasChanged(); });
    }

    public virtual void Dispose()
    {
        Signals.DataChanged -= OnChanged;
        _timer?.Dispose();
        GC.SuppressFinalize(this);
    }
}

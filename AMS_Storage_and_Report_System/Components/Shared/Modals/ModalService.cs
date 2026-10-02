// ModalService.cs — one service, one <ModalHost/>, every dialog in the app.
// Register:  builder.Services.AddScoped<ModalService>();   (scoped = per circuit)
// Place <ModalHost /> once in MainLayout.razor.
//
// Usage from any interactive component:
//   if (await Modal.ConfirmAsync("Remove item?", "Ballpen will be removed from your cart.",
//                                "Remove item", variant: ModalVariant.Danger)) { ... }
//   var reason = await Modal.PromptAsync("Reject RIS 2026-10-0004", "Tell the office what to fix.",
//                                        "Reason", "Reject request", minLength: 10);
//   await Modal.ShowComponentAsync<LoginForm>("Log in to continue",
//                                        new() { ["ReturnUrl"] = "/" + Nav.ToBaseRelativePath(Nav.Uri), ["PendingItemId"] = id });

using Microsoft.AspNetCore.Components;

namespace AMS_Storage_and_Report_System.Components.Shared.Modals;

public enum ModalVariant { Neutral, Info, Success, Warning, Danger }
public enum ModalSize { Small, Medium, Large, ExtraLarge }

public sealed class ModalOptions
{
    public required string Title { get; init; }
    public string? Message { get; init; }
    public RenderFragment? Body { get; init; }            // rich content (e.g. RIS summary)
    public ModalVariant Variant { get; init; } = ModalVariant.Neutral;
    public ModalSize Size { get; init; } = ModalSize.Medium;

    public string ConfirmText { get; init; } = "OK";
    public string? CancelText { get; init; }               // null → alert (single button)
    public bool DismissOnBackdrop { get; init; } = true;   // set false for destructive/long forms

    // Prompt mode (e.g. rejection reason)
    public bool ShowTextInput { get; init; }
    public string? InputLabel { get; init; }
    public string? InputPlaceholder { get; init; }
    public int InputMinLength { get; init; }
    public int InputMaxLength { get; init; } = 1000;

    /// Optional async work run when Confirm is clicked. Return null to close,
    /// or an error message to keep the modal open and show it inline.
    /// Lets "Approve request?" show a spinner and surface failures in place.
    public Func<string?, Task<string?>>? OnConfirmAsync { get; init; }

    // Custom component mode (login form, suggestion form, item detail)
    public Type? ComponentType { get; init; }
    public IDictionary<string, object?>? ComponentParameters { get; init; }
}

public sealed record ModalResult(bool Confirmed, string? Text = null, object? Data = null)
{
    public static ModalResult Cancel { get; } = new(false);
    public static ModalResult Ok(object? data = null) => new(true, Data: data);
}

/// Cascaded to custom modal content so it can close itself:
///   [CascadingParameter] public ModalInstance Instance { get; set; } = default!;
///   Instance.Close(ModalResult.Ok());
public sealed class ModalInstance
{
    private readonly ModalService _owner;
    internal ModalInstance(ModalService owner, ModalOptions options) { _owner = owner; Options = options; }

    public Guid Id { get; } = Guid.NewGuid();
    public ModalOptions Options { get; }
    internal TaskCompletionSource<ModalResult> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Close(ModalResult result) => _owner.Close(this, result);
    public void Cancel() => _owner.Close(this, ModalResult.Cancel);
}

public sealed class ModalService
{
    private readonly List<ModalInstance> _stack = [];
    public IReadOnlyList<ModalInstance> Open => _stack;
    public event Action? Changed;

    public Task<ModalResult> ShowAsync(ModalOptions options)
    {
        var m = new ModalInstance(this, options);
        _stack.Add(m);
        Changed?.Invoke();
        return m.Tcs.Task;
    }

    internal void Close(ModalInstance m, ModalResult result)
    {
        if (!_stack.Remove(m)) return;
        m.Tcs.TrySetResult(result);
        Changed?.Invoke();
    }

    internal void CancelAll()
    {
        foreach (var m in _stack.ToArray()) m.Tcs.TrySetResult(ModalResult.Cancel);
        _stack.Clear();
    }

    // ── Convenience API ──────────────────────────────────────────────

    public async Task<bool> ConfirmAsync(string title, string message,
        string confirmText = "Continue", string cancelText = "Cancel",
        ModalVariant variant = ModalVariant.Warning, RenderFragment? body = null,
        Func<string?, Task<string?>>? onConfirmAsync = null)
        => (await ShowAsync(new()
        {
            Title = title, Message = message, Body = body, Variant = variant,
            ConfirmText = confirmText, CancelText = cancelText,
            DismissOnBackdrop = variant != ModalVariant.Danger,
            OnConfirmAsync = onConfirmAsync,
        })).Confirmed;

    public Task AlertAsync(string title, string message, ModalVariant variant = ModalVariant.Info,
        string buttonText = "OK")
        => ShowAsync(new() { Title = title, Message = message, Variant = variant, ConfirmText = buttonText });

    public Task SuccessAsync(string title, string message) => AlertAsync(title, message, ModalVariant.Success, "Done");
    public Task ErrorAsync(string title, string message)   => AlertAsync(title, message, ModalVariant.Danger, "Got it");
    public Task WarningAsync(string title, string message) => AlertAsync(title, message, ModalVariant.Warning, "Got it");

    public async Task<string?> PromptAsync(string title, string message, string inputLabel,
        string confirmText, int minLength = 0, ModalVariant variant = ModalVariant.Danger,
        string? placeholder = null, Func<string?, Task<string?>>? onConfirmAsync = null)
    {
        var r = await ShowAsync(new()
        {
            Title = title, Message = message, Variant = variant,
            ShowTextInput = true, InputLabel = inputLabel, InputPlaceholder = placeholder,
            InputMinLength = minLength, ConfirmText = confirmText, CancelText = "Cancel",
            DismissOnBackdrop = false, OnConfirmAsync = onConfirmAsync,
        });
        return r.Confirmed ? r.Text : null;
    }

    public Task<ModalResult> ShowComponentAsync<TComponent>(string title,
        Dictionary<string, object?>? parameters = null, ModalSize size = ModalSize.Medium)
        where TComponent : IComponent
        => ShowAsync(new()
        {
            Title = title, Size = size,
            ComponentType = typeof(TComponent), ComponentParameters = parameters,
            CancelText = null, ConfirmText = "",   // content renders its own buttons
        });
}

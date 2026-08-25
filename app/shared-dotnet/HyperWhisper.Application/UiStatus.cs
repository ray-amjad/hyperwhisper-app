
namespace HyperWhisper.PortableApplication.ViewModels;

public sealed class UiStatus : ViewModelBase
{
    private bool _isBusy;
    private string _message = "Ready";
    private string? _errorCode;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string? ErrorCode { get => _errorCode; private set { if (Set(ref _errorCode, value)) Notify(nameof(HasError)); } }
    public bool HasError => ErrorCode != null;

    public void Busy(string message) { IsBusy = true; ErrorCode = null; Message = message; }
    public void Success(string message) { IsBusy = false; ErrorCode = null; Message = message; }
    public void Failure(string code, string message) { IsBusy = false; ErrorCode = code; Message = message; }
}

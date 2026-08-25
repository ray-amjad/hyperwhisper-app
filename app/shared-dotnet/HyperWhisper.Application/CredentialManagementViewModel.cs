using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;
using HyperWhisper.Platform.Abstractions;

namespace HyperWhisper.PortableApplication.ViewModels;

public sealed record CredentialAccount(string Account, string DisplayName, bool IsPresent);

public sealed class CredentialManagementViewModel : ViewModelBase
{
    private const string Resource = "HyperWhisper";
    private readonly ICredentialStore _store;
    private CredentialAccount? _selected;
    private string _secret = string.Empty;
    public CredentialManagementViewModel(ICredentialStore store)
    {
        _store = store;
        SaveCommand = new AsyncCommand(_ => SaveAsync());
        DeleteCommand = new AsyncCommand(_ => DeleteAsync());
        Refresh();
    }
    public ObservableCollection<CredentialAccount> Items { get; } = new();
    public CredentialAccount? Selected { get => _selected; set => Set(ref _selected, value); }
    public string Secret { get => _secret; set => Set(ref _secret, value); }
    public UiStatus Status { get; } = new();
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public void Refresh()
    {
        Items.Clear();
        foreach (var pair in Accounts)
        {
            var read = _store.Read(Resource, pair.Account);
            var present = read.IsSuccess && read.Value is { Length: > 0 } bytes;
            if (read.Value is { } sensitive) CryptographicOperations.ZeroMemory(sensitive);
            Items.Add(new(pair.Account, pair.Name, present));
        }
        Selected = Items.FirstOrDefault();
    }
    public void SelectAccount(string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        Selected = Items.FirstOrDefault(item => string.Equals(item.Account, account, StringComparison.Ordinal));
    }
    public Task SaveAsync()
    {
        if (Selected is null || string.IsNullOrWhiteSpace(Secret)) { Status.Failure("credentials.value_required", "Select a credential and enter its value."); return Task.CompletedTask; }
        var bytes = Encoding.UTF8.GetBytes(Secret.Trim());
        try
        {
            var result = _store.Write(Resource, Selected.Account, bytes);
            Secret = string.Empty;
            if (result.IsFailure) Status.Failure(result.Error!.Code, result.Error.Message);
            else { Refresh(); Status.Success("Credential saved securely"); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return Task.CompletedTask;
    }
    public Task DeleteAsync()
    {
        if (Selected is null) { Status.Failure("credentials.selection_required", "Select a credential."); return Task.CompletedTask; }
        var result = _store.Delete(Resource, Selected.Account);
        Secret = string.Empty;
        if (result.IsFailure) Status.Failure(result.Error!.Code, result.Error.Message);
        else { Refresh(); Status.Success("Credential deleted"); }
        return Task.CompletedTask;
    }
    private static readonly (string Account, string Name)[] Accounts =
    [
        ("OpenAIApiKey", "OpenAI API key"), ("AnthropicApiKey", "Anthropic API key"),
        ("CerebrasApiKey", "Cerebras API key"), ("GroqApiKey", "Groq API key"),
        ("DeepgramApiKey", "Deepgram API key"), ("AssemblyAIApiKey", "AssemblyAI API key"),
        ("ElevenLabsApiKey", "ElevenLabs API key"), ("MistralApiKey", "Mistral API key"),
        ("SonioxApiKey", "Soniox API key"), ("GeminiApiKey", "Gemini API key"),
        ("GrokApiKey", "xAI/Grok API key")
    ];
}

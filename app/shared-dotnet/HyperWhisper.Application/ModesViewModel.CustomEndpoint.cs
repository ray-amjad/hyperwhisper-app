using System.Security.Cryptography;
using System.Text;
using HyperWhisper.PortableApplication.Persistence;

namespace HyperWhisper.PortableApplication.ViewModels;

// The inline custom OpenAI-compatible post-processing endpoint: its fields and its secure storage.
public sealed partial class ModesViewModel
{
    private string _customEndpointName = string.Empty;
    private string _customEndpointUrl = string.Empty;
    private string _customEndpointModel = string.Empty;
    private string _customEndpointApiKey = string.Empty;
    private Guid? _customEndpointId;

    public string CustomEndpointName { get => _customEndpointName; set => Set(ref _customEndpointName, value ?? string.Empty); }
    public string CustomEndpointUrl { get => _customEndpointUrl; set => Set(ref _customEndpointUrl, value ?? string.Empty); }
    public string CustomEndpointModel { get => _customEndpointModel; set => Set(ref _customEndpointModel, value ?? string.Empty); }
    public string CustomEndpointApiKey { get => _customEndpointApiKey; set => Set(ref _customEndpointApiKey, value ?? string.Empty); }

    private bool ValidateCustomEndpoint()
    {
        // The URL and model rules come from the shared core (#282), the same
        // strict verdict the runtime and the Windows editor use. This method used
        // to spell out its own third variant of them.
        if (string.IsNullOrWhiteSpace(CustomEndpointName)
            || HyperWhisper.SharedCore.LlmPostProcessing.NormalizeCustomEndpoint(
                   CustomEndpointUrl ?? string.Empty, CustomEndpointModel ?? string.Empty).Status
               != HyperWhisper.SharedCore.PortableEndpointStatus.Valid)
        {
            Status.Failure("modes.custom_endpoint_invalid", "Enter a name, HTTP(S) endpoint URL without embedded credentials, and model.");
            return false;
        }
        if (_settings is null || _credentials is null)
        {
            Status.Failure("modes.custom_endpoint_unavailable", "Secure custom endpoint storage is unavailable.");
            return false;
        }
        return true;
    }

    private void PersistCustomEndpoint(Guid id)
    {
        var endpoints = (_settings!.Get<PortableCustomPostProcessingEndpoint[]>("customEndpoints", []) ?? []).ToList();
        var configured = new PortableCustomPostProcessingEndpoint(
            id, CustomEndpointName.Trim(), CustomEndpointUrl.Trim(), CustomEndpointModel.Trim());
        var index = endpoints.FindIndex(item => item.Id == id);
        if (index < 0) endpoints.Add(configured); else endpoints[index] = configured;
        _settings.Set("customEndpoints", endpoints);
        if (!string.IsNullOrWhiteSpace(CustomEndpointApiKey))
        {
            var bytes = Encoding.UTF8.GetBytes(CustomEndpointApiKey.Trim());
            try
            {
                var saved = _credentials!.Write("HyperWhisper", $"CustomEndpoint_{id:D}", bytes);
                if (saved.IsFailure) throw new InvalidOperationException(saved.Error!.Message);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        var persisted = _settings.Save();
        if (persisted.IsFailure) throw new InvalidOperationException(persisted.Error!.Message);
    }

    private void LoadCustomEndpoint(string? provider)
    {
        ClearCustomEndpointEditor();
        if (_settings is null || provider?.StartsWith("custom:", StringComparison.OrdinalIgnoreCase) != true
            || !Guid.TryParse(provider[7..], out var id)) return;
        var configured = (_settings.Get<PortableCustomPostProcessingEndpoint[]>("customEndpoints", []) ?? [])
            .FirstOrDefault(item => item.Id == id);
        if (configured is null) return;
        _customEndpointId = id;
        CustomEndpointName = configured.Name;
        CustomEndpointUrl = configured.EndpointUrl;
        CustomEndpointModel = configured.ModelName;
    }

    private void ClearCustomEndpointEditor()
    {
        _customEndpointId = null;
        CustomEndpointName = CustomEndpointUrl = CustomEndpointModel = CustomEndpointApiKey = string.Empty;
    }
}

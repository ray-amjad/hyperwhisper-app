using System.Collections.Generic;
using HyperWhisper.Models;

namespace HyperWhisper.Services;

public partial class SettingsService
{
    // =========================================================================
    // CUSTOM ENDPOINTS
    // =========================================================================

    /// <summary>
    /// Gets or sets the list of custom OpenAI-compatible endpoints for post-processing.
    /// </summary>
    public List<CustomPostProcessingEndpoint> CustomEndpoints
    {
        get => _settings.CustomEndpoints ?? [];
        set
        {
            _settings.CustomEndpoints = value;
            Save();
            NotifySettingsChanged();
        }
    }
}

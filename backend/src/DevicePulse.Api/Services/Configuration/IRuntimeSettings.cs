namespace DevicePulse.Api.Services.Configuration;

/// <summary>
/// The single typed entry point for reading runtime business settings (Appendix C item 6).
/// Services ask for <c>GetInt(SettingKeys.OfflineTimeoutSeconds)</c>, never for a raw string by
/// an inline key. One place owns parsing, defaults and caching, so a malformed value in the
/// database degrades to the compiled-in default instead of throwing somewhere unrelated.
/// </summary>
public interface IRuntimeSettings
{
    int GetInt(string key);
    double GetDouble(string key);
    bool GetBool(string key);
    string GetString(string key);

    /// <summary>Drops the cache so the next read sees a just-written value. Called by the settings service on every accepted edit.</summary>
    void Invalidate();
}

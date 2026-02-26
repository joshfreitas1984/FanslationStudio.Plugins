namespace FanslationStudio.Plugins.Shared;

public interface IPluginLogger
{
    void LogInfo(string message);
    void LogWarning(string message);
    void LogError(string message);
    void LogDebug(string message);
    void LogMessage(string message);
    void LogFatal(string message);
}

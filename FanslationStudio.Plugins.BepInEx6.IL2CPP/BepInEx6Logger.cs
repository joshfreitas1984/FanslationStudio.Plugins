using BepInEx.Logging;
using FanslationStudio.Plugins.Shared;

public class BepInEx6Logger : IPluginLogger
{
    private ManualLogSource _logger;

    public BepInEx6Logger(ManualLogSource logger)
    {
        _logger = logger;
    }

    public void LogInfo(string message) => _logger.LogInfo(message);
    public void LogWarning(string message) => _logger.LogWarning(message);
    public void LogError(string message) => _logger.LogError(message);
    public void LogDebug(string message) => _logger.LogDebug(message);
    public void LogMessage(string message) => _logger.LogMessage(message);
    public void LogFatal(string message) => _logger.LogFatal(message);
}
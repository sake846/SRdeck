
namespace SRdeck.Configuration;

public sealed record SettingsPersistenceIssue(
    string FilePath, string Operation, string Message, bool IsError, Exception? Exception = null);

/// <summary>Optional diagnostics capability; existing settings service implementations remain compatible.</summary>
public interface ISettingsPersistenceNotifications
{
    event Action<SettingsPersistenceIssue>? PersistenceIssue;
}

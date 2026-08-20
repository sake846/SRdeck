namespace SRdeck.Configuration;

public interface ILastStateService
{
    LastState LoadLastState();
    void SaveLastState(LastState state);
    void BackupLastState();
}

using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Messages;
using SRdeck.Models;

namespace SRdeck.Services;

public interface IRadioControlUpdatePublisher
{
    void Publish(RadioControl control, bool resetMainViewZoom = false);
}

public sealed class RadioControlUpdatePublisher : IRadioControlUpdatePublisher
{
    // A newly activated plugin may immediately restore a narrow display span.
    // Apply its tuning before that zoom can suppress the normal cycle-based retune.
    public void Publish(RadioControl control, bool resetMainViewZoom = false) =>
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(
            control,
            ResetMainViewZoom: resetMainViewZoom,
            ApplyFrequencyImmediately: true));
}
